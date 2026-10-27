using Microsoft.AspNetCore.SignalR;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Models;
using RummiHub.Web.Rendering;
using RummiHub.Web.Services;

namespace RummiHub.Web.Hubs;

public partial class GameHub(
    IGameManager gameManager,
    IHttpClientFactory httpClientFactory,
    SolveResultSubscriber solveResultSubscriber,
    IHubContext<GameHub> hubContext,
    ILogger<GameHub> logger) : Hub
{
    /// <summary>
    /// Shortest watchable pause before an AI seat plays its automatic move, so humans can
    /// follow the action rather than seeing the board jump instantly. A move that changed the
    /// board waits longer: its reading window (<see cref="BoardPacing.ReadingMs"/>).
    /// </summary>
    private static readonly TimeSpan AutoPlayDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the played-tiles highlight stays on AFTER the board animation ends; the client
    /// then fades it out, so the previous move's yellow never lingers forever. It outlasts the
    /// longest reading window, so a move stays lit until the next one lands.
    /// </summary>
    private const int HighlightLingerMs = BoardPacing.MaxReadMs + 1000;

    // ========== LOBBY PHASE ==========

    public async Task GetAvailableGameIds()
    {
        try
        {
            var ids = await gameManager.GetProposedSeedsAsync();
            await Clients.Caller.SendAsync(HubEvents.AvailableGameIds, ids);
        }
        catch (SeedCatalogUnavailableException ex)
        {
            LogHubError(ex, nameof(GetAvailableGameIds), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.AvailableGameIds, Array.Empty<string>());
            await Clients.Caller.SendAsync(HubEvents.Error,
                "The proposed seed list is unavailable. You can still start a random game, or paste a 64-character seed.");
        }
    }

    /// <summary>
    /// Adds the game's distribution seed to the shared list. Any seated player can do this
    /// while the game is in progress. The value is the one the server dealt — never a string
    /// supplied by the caller — and it is not sent back to the browser.
    /// </summary>
    public async Task SaveSeed(string gameId)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;
            var game = gameManager.GetGame(gameId);
            var seed = SeedPublication.ForSeatedPlayer(game, Context.ConnectionId);
            if (seed is null)
                return;

            var result = await gameManager.AddSeedAsync(seed);
            var status = result.Status switch
            {
                SeedAddStatus.Added => "added",
                SeedAddStatus.AlreadyPresent => "already",
                _ => "rejected"
            };
            await Clients.Caller.SendAsync(HubEvents.SeedSaved, status, result.Error ?? "");
        }
        catch (SeedCatalogUnavailableException ex)
        {
            LogHubError(ex, nameof(SaveSeed), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.SeedSaved, "unavailable", "");
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(SaveSeed), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.SeedSaved, "rejected", "");
        }
    }

    public async Task CreateGame(string playerName, string? gameId = null)
    {
        try
        {
            playerName = playerName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Player name is required.");
                return;
            }

            // The second argument is a distribution seed, not a game code. Empty deals at
            // random. A 64-character hex seed may be pasted. An older short id must already
            // be on the shared list.
            var requestedSeed = gameId?.Trim();
            var game = await gameManager.CreateGame(playerName, Context.ConnectionId, requestedSeed);
            var seatToken = game.GetPlayers().Single().SeatToken;
            await Groups.AddToGroupAsync(Context.ConnectionId, game.Id);
            // Seat secret goes to the creator only — never on the shared player list.
            await Clients.Caller.SendAsync(HubEvents.SeatIssued, game.Id, seatToken);
            await Clients.Caller.SendAsync(HubEvents.GameCreated, game.Id, game.GetPlayerNames());
        }
        catch (SeedInputException ex)
        {
            await Clients.Caller.SendAsync(HubEvents.Error, ex.Message);
        }
        catch (SeedCatalogUnavailableException ex)
        {
            LogHubError(ex, nameof(CreateGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error,
                "The proposed seed list is unavailable, so an older short seed cannot be checked. A random game, or a pasted 64-character seed, still works.");
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(CreateGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error,
                "An error occurred while creating the game.");
        }
    }

    public async Task JoinGame(string gameId, string playerName)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;
            playerName = playerName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(playerName))
            {
                await Clients.Caller.SendAsync(HubEvents.Error,
                    "Game code and player name are required.");
                return;
            }

            var game = gameManager.GetGame(gameId);
            if (game == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Game not found.");
                return;
            }

            if (game.HasPlayerWithName(playerName))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "This name is already taken in this game.");
                return;
            }

            if (game.PlayerCount >= GameLimits.MaxPlayers)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, GameLimits.TooManyPlayersMessage);
                return;
            }

            var player = gameManager.AddPlayer(gameId, playerName, Context.ConnectionId);
            if (player == null)
            {
                var message = game.PlayerCount >= GameLimits.MaxPlayers
                    ? GameLimits.TooManyPlayersMessage
                    : "Could not join the game.";
                await Clients.Caller.SendAsync(HubEvents.Error, message);
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
            await Clients.Caller.SendAsync(HubEvents.SeatIssued, gameId, player.SeatToken);
            await Clients.Group(gameId).SendAsync(HubEvents.PlayerJoined, player.Name, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(JoinGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "An error occurred while joining the game.");
        }
    }

    /// <summary>
    /// Host adds an AI (auto-play) seat to the game while still in the lobby.
    /// </summary>
    public async Task AddAiPlayer(string gameId)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;

            var game = gameManager.GetGame(gameId);
            if (game == null || game.HostConnectionId != Context.ConnectionId || game.IsStarted)
                return;

            if (game.PlayerCount >= GameLimits.MaxPlayers)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, GameLimits.TooManyPlayersMessage);
                return;
            }

            var player = gameManager.AddAiPlayer(gameId);
            if (player == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Could not add an AI player.");
                return;
            }

            await Clients.Group(gameId).SendAsync(HubEvents.PlayerJoined, player.Name, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(AddAiPlayer), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "An error occurred while adding an AI player.");
        }
    }

    public async Task StartGame(string gameId)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;

            var game = gameManager.GetGame(gameId);
            if (game == null || game.HostConnectionId != Context.ConnectionId)
                return;

            if (game.IsStarted) return;

            if (game.PlayerCount < GameLimits.MinPlayers)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "At least 2 players are required to start.");
                return;
            }

            if (game.PlayerCount > GameLimits.MaxPlayers)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, GameLimits.TooManyPlayersMessage);
                return;
            }

            // Initialize the engine server-side
            if (!gameManager.InitializeEngine(gameId))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Error initializing the game.");
                return;
            }

            game.IsStarted = true;
            LogGameStarted(gameId);

            // Broadcast: all lobby clients navigate to the game page
            await Clients.Group(gameId).SendAsync(HubEvents.GameStarted, gameId, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(StartGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error,
                "An error occurred while starting the game.");
        }
    }

    // ========== GAME PHASE ==========

    /// <summary>
    /// Called by GamePage when it connects. Maps the new connection to the player,
    /// adds to the SignalR group, sends initial game state + rack.
    /// </summary>
    public async Task JoinGameSession(string gameId, string playerName, string? seatToken)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;
            playerName = playerName?.Trim() ?? string.Empty;
            seatToken = seatToken?.Trim() ?? string.Empty;

            var game = gameManager.GetGame(gameId);
            if (game?.Engine == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Game not found or not started.");
                return;
            }

            // The seat secret was issued to this player at create/join and stored in the
            // browser tab. Knowing the game id and the display name is not enough.
            if (!gameManager.UpdatePlayerConnection(gameId, playerName, seatToken, Context.ConnectionId))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Could not rejoin this seat.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

            // Make sure an optimal solve is in flight for whoever's turn it currently is.
            // Idempotent — won't relaunch if one already runs for this (turn, playerIndex).
            await game.ActionLock.WaitAsync();
            try
            {
                StartOptimalSolveForCurrentPlayerIfNeeded(game);
                // Seed the alignment baseline with the initial board so the first move animates
                // from it (and isn't treated as a from-nothing board with no animation).
                game.LastBroadcastBoard ??= game.Engine!.Board;
            }
            finally
            {
                game.ActionLock.Release();
            }

            // Send initial game state
            var state = BuildGameState(game);
            await Clients.Caller.SendAsync(HubEvents.GameStateUpdated, state);

            // Send this player's private rack
            var playerIndex = game.FindPlayerIndexByConnection(Context.ConnectionId);
            if (playerIndex.HasValue)
            {
                var rack = GetRackTiles(game.Engine, playerIndex.Value);
                await Clients.Caller.SendAsync(HubEvents.RackUpdated, rack);

                // If it's this player's turn and the background solve is already done
                // (reconnect after a slow start), send the indicator immediately.
                var pending = game.PendingOptimalSolve;
                if (playerIndex.Value == game.Engine.PlayerIndex
                    && pending is { Task.IsCompletedSuccessfully: true })
                {
                    await Clients.Caller.SendAsync(
                        HubEvents.OptimalReady, OptimalTiles(pending.Task.Result));
                }
            }

            LogPlayerJoinedSession(playerName, gameId);
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(JoinGameSession), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "Error connecting to the game.");
        }
    }

    /// <summary>
    /// Human player submits selected tiles to play. With <paramref name="autoContinue"/>
    /// the turn advances immediately; without it, the move is applied and broadcast but
    /// the turn waits for <see cref="ConfirmEndTurn"/> so the player can watch (and
    /// replay) the animation first.
    /// </summary>
    public async Task PlayTiles(string gameId, List<Tile> selectedTiles, bool autoContinue = true)
    {
        var game = gameManager.GetGame(gameId);
        if (game?.Engine == null) return;

        await game.ActionLock.WaitAsync();
        try
        {
            // Validate it's this player's turn
            var playerIndex = game.FindPlayerIndexByConnection(Context.ConnectionId);
            if (playerIndex == null || playerIndex.Value != game.Engine.PlayerIndex)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "It's not your turn.");
                return;
            }

            // A review-mode move was already applied this turn — only confirming remains.
            if (game.PendingTurnConfirmation is not null)
            {
                await Clients.Caller.SendAsync(HubEvents.InvalidPlay,
                    "You already played this turn — confirm to pass to the next player.");
                return;
            }

            // Each attempt is a solver round trip, and the solver serves every table.
            if (!game.TryStartPlayAttempt(playerIndex.Value, DateTimeOffset.UtcNow))
            {
                await Clients.Caller.SendAsync(HubEvents.InvalidPlay, GameLimits.TooManyPlayAttemptsMessage);
                return;
            }

            // Validate via the distributed solver. A selection not taken from this rack is
            // refused before any request, its error relayed by the not-found branch below.
            var client = httpClientFactory.CreateClient("apiservice");
            var result = await game.Engine.ValidateHumanPlay(
                selectedTiles, client, solveResultSubscriber, Context.ConnectionAborted);

            if (result.Found)
            {
                var totalPlayed = result.TilesToPlay.Count + result.JokersToPlay;
                if (totalPlayed == selectedTiles.Count)
                {
                    // Apply the move
                    game.Engine.ApplySolveResult(result);

                    // Send updated rack to this player
                    var rack = GetRackTiles(game.Engine, playerIndex.Value);
                    await Clients.Caller.SendAsync(HubEvents.RackUpdated, rack);

                    // Advance the turn (or, in review mode, broadcast the new board and
                    // pause until the player confirms), and arm the next solve.
                    var boardBefore = game.LastBroadcastBoard;
                    if (autoContinue || game.Engine.IsGameOver)
                        await AdvanceTurnAndNotifyAsync(game);
                    else
                        await PauseForReviewAsync(game, playerIndex.Value);

                    // Highlight, for everyone at the table, the tiles played from the rack and
                    // the sets the move changed. Indices are on the aligned board just
                    // broadcast, so when the played tile has an identical twin already resting
                    // there, the new copy lights up. Sent after the state so clients map
                    // indices onto it. Shown for the animation plus a reading window, then
                    // cleared.
                    await BroadcastMoveHighlightAsync(game, boardBefore, OptimalTiles(result));
                }
                else
                {
                    await Clients.Caller.SendAsync(HubEvents.InvalidPlay,
                        "Not all selected tiles could be placed.");
                }
            }
            else
            {
                var msg = !game.Engine.CurrentPlayer.HasPlayed
                    ? $"First move must score at least 30 points. Current score: {result.Score}."
                    : "Invalid combination with the current board.";

                if (!string.IsNullOrEmpty(result.Error))
                    msg = result.Error;

                await Clients.Caller.SendAsync(HubEvents.InvalidPlay, msg);
            }
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(PlayTiles), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "Error validating the move.");
        }
        finally
        {
            game.ActionLock.Release();
        }
    }

    /// <summary>
    /// Human player draws a tile. Turn advances immediately.
    /// </summary>
    public async Task DrawTile(string gameId)
    {
        var game = gameManager.GetGame(gameId);
        if (game?.Engine == null) return;

        await game.ActionLock.WaitAsync();
        try
        {
            var playerIndex = game.FindPlayerIndexByConnection(Context.ConnectionId);
            if (playerIndex == null || playerIndex.Value != game.Engine.PlayerIndex)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "It's not your turn.");
                return;
            }

            if (game.PendingTurnConfirmation is not null)
            {
                await Clients.Caller.SendAsync(HubEvents.InvalidPlay,
                    "You already played this turn — confirm to pass to the next player.");
                return;
            }

            if (game.Engine.TilePoolCount == 0)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "No tiles left in the pool.");
                return;
            }

            var drawnTile = game.Engine.DrawTile();

            // Send drawn tile + updated rack to this player
            await Clients.Caller.SendAsync(HubEvents.TileDrawn, drawnTile);
            var rack = GetRackTiles(game.Engine, playerIndex.Value);
            await Clients.Caller.SendAsync(HubEvents.RackUpdated, rack);

            // Advance the turn, broadcast the new state, and arm the next solve.
            await AdvanceTurnAndNotifyAsync(game);
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(DrawTile), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "Error drawing a tile.");
        }
        finally
        {
            game.ActionLock.Release();
        }
    }

    /// <summary>
    /// Ends a "play &amp; review" turn: the move was already applied and broadcast by
    /// <see cref="PlayTiles"/>; this just advances to the next player.
    /// </summary>
    public async Task ConfirmEndTurn(string gameId)
    {
        var game = gameManager.GetGame(gameId);
        if (game?.Engine == null) return;

        await game.ActionLock.WaitAsync();
        try
        {
            var pending = game.PendingTurnConfirmation;
            var playerIndex = game.FindPlayerIndexByConnection(Context.ConnectionId);
            if (pending is null || playerIndex != pending.PlayerIndex) return;

            game.PendingTurnConfirmation = null;
            await AdvanceTurnAndNotifyAsync(game);
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(ConfirmEndTurn), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "Error ending the turn.");
        }
        finally
        {
            game.ActionLock.Release();
        }
    }

    // ========== DISCONNECT ==========

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var game = gameManager.FindGameByConnection(Context.ConnectionId);
            if (game != null)
            {
                // Don't remove players from started games — they can reconnect
                if (!game.IsStarted)
                {
                    var gameId = game.Id;
                    gameManager.RemovePlayer(Context.ConnectionId);

                    var updatedGame = gameManager.GetGame(gameId);
                    if (updatedGame != null)
                        await Clients.Group(gameId)
                            .SendAsync(HubEvents.PlayerLeft, updatedGame.GetPlayerNames());
                }
            }
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(OnDisconnectedAsync), Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    // ========== HELPERS ==========

    private static GameStateDto BuildGameState(Game game)
    {
        var engine = game.Engine!;
        var winnerName = engine.IsGameOver ? engine.WinnerName : null;

        return new GameStateDto
        {
            GameId = game.Id,
            // Every player in the group receives this DTO: seat secrets stay off it, and the
            // seed (which rebuilds every rack and the draw order) only appears at game over.
            DistributionSeed = game.RevealedDistributionSeed,
            Players = engine.Players.Select(p => new PlayerInfoDto
            {
                Name = p.Name,
                TileCount = p.Rack.Tiles.Count + p.Rack.Jokers,
                HasPlayed = p.HasPlayed
            }).ToList(),
            CurrentPlayerIndex = engine.PlayerIndex,
            Turn = engine.Turn,
            // LastBroadcastBoard is the post-move board seated so unchanged sets stay put.
            // engine.Board mirrors it, which is what the next solve stabilizes against.
            Board = game.LastBroadcastBoard ?? engine.Board,
            IsGameOver = engine.IsGameOver,
            WinnerName = winnerName,
            TilePoolCount = engine.TilePoolCount,
            AwaitingConfirmation = game.PendingTurnConfirmation is not null
        };
    }

    private static List<Tile> GetRackTiles(GameEngine engine, int playerIndex)
    {
        return engine.Players[playerIndex].Rack.GetAllTilesIncludingJokers().ToList();
    }

    /// <summary>
    /// Shared turn-completion tail used by every action (human play/draw and AI auto-play):
    /// advances to the next player, broadcasts the new state, sends that player their rack,
    /// and arms their optimal solve. Uses <c>hubContext</c> so it works both inside and
    /// outside a hub invocation. Callers must hold <see cref="Game.ActionLock"/>.
    /// </summary>
    private async Task AdvanceTurnAndNotifyAsync(Game game)
    {
        var engine = game.Engine!;
        engine.AdvanceToNextPlayer();
        SeatBoard(game);

        var state = BuildGameState(game);
        await hubContext.Clients.Group(game.Id).SendAsync(HubEvents.GameStateUpdated, state);

        await SendCurrentPlayerRack(game);

        StartOptimalSolveForCurrentPlayerIfNeeded(game);
    }

    /// <summary>
    /// Review-mode tail of a human play: aligns and broadcasts the post-move board (so
    /// everyone watches the animation) but keeps the turn on the same player until they
    /// call <see cref="ConfirmEndTurn"/>. Callers must hold <see cref="Game.ActionLock"/>.
    /// </summary>
    private async Task PauseForReviewAsync(Game game, int playerIndex)
    {
        SeatBoard(game);
        game.PendingTurnConfirmation = new PendingTurnConfirmation(game.Engine!.Turn, playerIndex);

        var state = BuildGameState(game);
        await hubContext.Clients.Group(game.Id).SendAsync(HubEvents.GameStateUpdated, state);
    }

    /// <summary>
    /// Aligns the post-move board to the one currently shown so unchanged sets keep their
    /// place, and records how long clients take to play the change out — the board animation
    /// (play → make room → place), then the reading window — so the next AI move waits for
    /// both. Callers must hold <see cref="Game.ActionLock"/>.
    /// </summary>
    private static void SeatBoard(Game game)
    {
        var engine = game.Engine!;
        var previous = game.LastBroadcastBoard;
        var aligned = previous is null ? engine.Board : BoardAlignment.Align(previous, engine.Board);
        game.PendingAnimationMs = previous is null ? 0 : BoardPacing.EstimateMs(previous, aligned);
        game.PendingReadingMs = previous is null ? 0 : BoardPacing.ReadingMs(previous, aligned);
        game.LastBroadcastBoard = aligned;
        engine.Board = aligned;
    }

    /// <summary>
    /// Sends the current player their rack. No-op for AI seats, which have no connection.
    /// </summary>
    private async Task SendCurrentPlayerRack(Game game)
    {
        var engine = game.Engine!;
        var currentPlayerIndex = engine.PlayerIndex;
        var lobbyPlayers = game.GetPlayers();
        if (currentPlayerIndex >= lobbyPlayers.Count) return;

        var connectionId = lobbyPlayers[currentPlayerIndex].ConnectionId;
        if (string.IsNullOrEmpty(connectionId)) return;

        var rack = GetRackTiles(engine, currentPlayerIndex);
        await hubContext.Clients.Client(connectionId).SendAsync(HubEvents.RackUpdated, rack);
    }

    // ========== OPTIMAL-PLAY HINT ==========

    /// <summary>
    /// Kicks off a background solve from the current player's full hand, unless one
    /// is already in flight for this (turn, playerIndex). Idempotent on reconnects.
    /// Must be called inside ActionLock.
    /// </summary>
    private void StartOptimalSolveForCurrentPlayerIfNeeded(Game game)
    {
        var engine = game.Engine!;
        if (engine.IsGameOver) return;
        // Mid-review the player already acted: their next-turn solve is armed on confirm.
        if (game.PendingTurnConfirmation is not null) return;

        var existing = game.PendingOptimalSolve;
        if (existing is not null
            && existing.Turn == engine.Turn
            && existing.PlayerIndex == engine.PlayerIndex
            && !existing.Task.IsCompleted)
        {
            return;
        }

        existing?.Cts.Cancel();

        var cts = new CancellationTokenSource();
        var client = httpClientFactory.CreateClient("apiservice");
        var turn = engine.Turn;
        var playerIndex = engine.PlayerIndex;

        // Call without Task.Run so the synchronous prefix (which snapshots Hand/Board
        // into the SolveRequest) runs on the hub thread while we still hold ActionLock.
        var task = RunOptimalSolveAsync(engine, client, cts.Token, game.Id, turn, playerIndex);

        game.PendingOptimalSolve = new PendingOptimalSolve(turn, playerIndex, task, cts);

        // Fire-and-forget: when the solve is done, hand the tiles to a human so a Hint
        // click can select them, or play them if this seat is an AI.
        _ = NotifyOptimalReadyWhenDoneAsync(game, turn, playerIndex, task, cts.Token);
    }

    private async Task NotifyOptimalReadyWhenDoneAsync(
        Game game, int turn, int playerIndex, Task<SolveResponse> task, CancellationToken ct)
    {
        try
        {
            await task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Helper swallows internal failures and returns Found=false; we still
            // signal readiness so the spinner stops.
        }

        // Skip if this solve is no longer current (turn advanced, or replaced).
        var pending = game.PendingOptimalSolve;
        if (pending is null || !ReferenceEquals(pending.Task, task)) return;
        if (game.Engine is not { } engine) return;
        if (engine.IsGameOver || engine.Turn != turn || engine.PlayerIndex != playerIndex) return;

        // AI seats play automatically; humans get the tiles for an on-demand Hint click.
        if (engine.CurrentPlayer.IsAuto)
        {
            await PerformAutoPlayAsync(game, turn, playerIndex, task, ct);
            return;
        }

        // Resolve the player's current connection (may have changed via reconnect).
        var lobbyPlayers = game.GetPlayers();
        if (playerIndex >= lobbyPlayers.Count) return;
        var connectionId = lobbyPlayers[playerIndex].ConnectionId;
        if (string.IsNullOrEmpty(connectionId)) return;

        try
        {
            // The optimal tiles ride along so a Hint click can select them.
            var result = task.IsCompletedSuccessfully ? task.Result : null;
            await hubContext.Clients.Client(connectionId)
                .SendAsync(HubEvents.OptimalReady, OptimalTiles(result));
        }
        catch (Exception ex)
        {
            LogOptimalReadyNotifyFailed(ex, game.Id, turn, playerIndex);
        }
    }

    /// <summary>
    /// Plays the AI seat's optimal move automatically after a short, watchable delay:
    /// applies the solved move (or draws when there's nothing to play), advances the turn,
    /// broadcasts the new state, and re-arms the chain for the next player (which auto-plays
    /// too if it is also an AI). Runs outside a hub invocation, so it uses <c>hubContext</c>.
    /// </summary>
    private async Task PerformAutoPlayAsync(
        Game game, int turn, int playerIndex, Task<SolveResponse> task, CancellationToken ct)
    {
        try
        {
            // Wait for the currently-shown transition's animation to finish, then for its
            // reading window (at least the watchable pause), so an AI move never lands on top
            // of the previous one before the table has had time to read it.
            var pause = TimeSpan.FromMilliseconds(game.PendingReadingMs);
            await Task.Delay(TimeSpan.FromMilliseconds(game.PendingAnimationMs)
                             + (pause > AutoPlayDelay ? pause : AutoPlayDelay), ct);
            await game.ActionLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return; // turn was replaced/cancelled before we could act
        }

        try
        {
            // Re-validate under the lock: this solve must still be the current one for the
            // same turn/player. The turn changing once we apply makes any stale task bail,
            // which prevents a double move (e.g. after a reconnect relaunched the solve).
            var pending = game.PendingOptimalSolve;
            if (pending is null || !ReferenceEquals(pending.Task, task)) return;
            if (game.Engine is not { } engine) return;
            if (engine.IsGameOver || engine.Turn != turn || engine.PlayerIndex != playerIndex) return;
            if (!engine.CurrentPlayer.IsAuto) return;

            var result = task.IsCompletedSuccessfully ? task.Result : null;
            var hasMove = result is { Found: true }
                          && result.TilesToPlay.Count + result.JokersToPlay > 0;

            var boardBefore = game.LastBroadcastBoard;
            if (hasMove)
                engine.ApplySolveResult(result!);
            else if (engine.TilePoolCount > 0)
                engine.DrawTile();
            // else: nothing to play and an empty pool — the turn simply passes.

            // Advance the turn, broadcast, and arm the next solve. If that player is also
            // an AI, the chain continues automatically.
            await AdvanceTurnAndNotifyAsync(game);
            if (hasMove)
                await BroadcastMoveHighlightAsync(game, boardBefore, OptimalTiles(result));
        }
        catch (Exception ex)
        {
            LogAutoPlayFailed(ex, game.Id, turn, playerIndex);
        }
        finally
        {
            game.ActionLock.Release();
        }
    }

    private async Task<SolveResponse> RunOptimalSolveAsync(
        GameEngine engine, HttpClient client, CancellationToken ct,
        string gameId, int turn, int playerIndex)
    {
        try
        {
            return await engine.SolveForCurrentPlayer(client, solveResultSubscriber, ct);
        }
        catch (Exception ex)
        {
            LogOptimalSolveFailed(ex, gameId, turn, playerIndex);
            return new SolveResponse { RequestId = "failed", Found = false };
        }
    }

    /// <summary>
    /// Tells every seat which tiles came out of the mover's rack and which sets the move
    /// changed, so an AI turn is as readable as a human one. Indices refer to the board just
    /// broadcast.
    /// </summary>
    private async Task BroadcastMoveHighlightAsync(Game game, Solution? boardBefore, List<Tile> played)
    {
        if (boardBefore is null || game.LastBroadcastBoard is not { } next) return;

        await hubContext.Clients.Group(game.Id).SendAsync(
            HubEvents.HighlightTilesUpdated,
            BoardPacing.PlayedIndices(boardBefore, next, played),
            game.PendingAnimationMs + HighlightLingerMs,
            BoardPacing.ModifiedSetIndices(boardBefore, next));
    }

    /// <summary>Flattens a solve result into the tile list it plays (jokers included).</summary>
    private static List<Tile> OptimalTiles(SolveResponse? result) =>
        result is { Found: true }
            ? [..result.TilesToPlay,
               ..Enumerable.Range(0, result.JokersToPlay).Select(_ => new Tile(isJoker: true))]
            : [];

    // ========== LOGGING ==========

    [LoggerMessage(Level = LogLevel.Information, Message = "Game {GameId} started")]
    private partial void LogGameStarted(string gameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Player {PlayerName} joined game session {GameId}")]
    private partial void LogPlayerJoinedSession(string playerName, string gameId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {MethodName} for connection {ConnectionId}")]
    private partial void LogHubError(Exception ex, string methodName, string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optimal solve failed for game {GameId} turn {Turn} player {PlayerIndex}")]
    private partial void LogOptimalSolveFailed(Exception ex, string gameId, int turn, int playerIndex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to notify optimal-ready for game {GameId} turn {Turn} player {PlayerIndex}")]
    private partial void LogOptimalReadyNotifyFailed(Exception ex, string gameId, int turn, int playerIndex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auto-play failed for game {GameId} turn {Turn} player {PlayerIndex}")]
    private partial void LogAutoPlayFailed(Exception ex, string gameId, int turn, int playerIndex);
}
