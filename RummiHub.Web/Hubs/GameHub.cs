using Microsoft.AspNetCore.SignalR;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Models;
using RummiHub.Web.Services;

namespace RummiHub.Web.Hubs;

public partial class GameHub(
    IGameManager gameManager,
    IHttpClientFactory httpClientFactory,
    SolveResultSubscriber solveResultSubscriber,
    ILogger<GameHub> logger) : Hub
{
    // ========== LOBBY PHASE ==========

    public async Task GetAvailableGameIds()
    {
        var ids = gameManager.GetAvailableGameIds();
        await Clients.Caller.SendAsync(HubEvents.AvailableGameIds, ids);
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

            var game = gameManager.CreateGame(playerName, Context.ConnectionId, gameId?.Trim()); // gameId = distribution seed
            await Groups.AddToGroupAsync(Context.ConnectionId, game.Id);
            await Clients.Caller.SendAsync(HubEvents.GameCreated, game.Id, game.GetPlayerNames());
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

            var player = gameManager.AddPlayer(gameId, playerName, Context.ConnectionId);
            if (player == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Could not join the game.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
            await Clients.Group(gameId).SendAsync(HubEvents.PlayerJoined, player.Name, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(JoinGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "An error occurred while joining the game.");
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

            if (game.PlayerCount < 2)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "At least 2 players are required to start.");
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
    public async Task JoinGameSession(string gameId, string playerName)
    {
        try
        {
            var game = gameManager.GetGame(gameId);
            if (game?.Engine == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Game not found or not started.");
                return;
            }

            // Update connection mapping (player navigated from lobby → game page)
            if (!gameManager.UpdatePlayerConnection(gameId, playerName, Context.ConnectionId))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Player not found in this game.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

            // Make sure an optimal solve is in flight for whoever's turn it currently is.
            // Idempotent — won't relaunch if one already runs for this (turn, playerIndex).
            await game.ActionLock.WaitAsync();
            try
            {
                StartOptimalSolveForCurrentPlayerIfNeeded(game);
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
    /// Human player submits selected tiles to play.
    /// </summary>
    public async Task PlayTiles(string gameId, List<Tile> selectedTiles)
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

            // Validate via the distributed solver
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

                    // Send highlight privately: only the player who played sees the red borders
                    var playedTiles = game.Engine.Players[playerIndex.Value].TilesToPlay;
                    await Clients.Caller.SendAsync(HubEvents.HighlightTilesUpdated, playedTiles);

                    // Send the optimal play that was being computed in background since the
                    // start of this turn (against the player's pre-action hand).
                    var turnAtAction = game.Engine.Turn;
                    var playerAtAction = playerIndex.Value;
                    await SendOptimalPlayToCallerAsync(
                        game, turnAtAction, playerAtAction, Context.ConnectionAborted);

                    // Advance turn
                    game.Engine.AdvanceToNextPlayer();

                    // Broadcast new game state to all (no highlight info — it's private)
                    var state = BuildGameState(game);
                    await Clients.Group(gameId).SendAsync(HubEvents.GameStateUpdated, state);

                    // Send the next player their rack (in case they just connected and need it)
                    await SendCurrentPlayerRack(game, gameId);

                    // Kick off the next player's optimal solve in the background.
                    StartOptimalSolveForCurrentPlayerIfNeeded(game);
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

            // Send the optimal play that was being computed in background since the
            // start of this turn (what the solver would have played from the pre-draw hand).
            var turnAtAction = game.Engine.Turn;
            var playerAtAction = playerIndex.Value;
            await SendOptimalPlayToCallerAsync(
                game, turnAtAction, playerAtAction, Context.ConnectionAborted);

            // Advance turn
            game.Engine.AdvanceToNextPlayer();

            // Broadcast state to all
            var state = BuildGameState(game);
            await Clients.Group(gameId).SendAsync(HubEvents.GameStateUpdated, state);

            // Send next player their rack
            await SendCurrentPlayerRack(game, gameId);

            // Kick off the next player's optimal solve in the background.
            StartOptimalSolveForCurrentPlayerIfNeeded(game);
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
            DistributionSeed = game.DistributionSeed,
            Players = engine.Players.Select(p => new PlayerInfoDto
            {
                Name = p.Name,
                TileCount = p.Rack.Tiles.Count + p.Rack.Jokers,
                HasPlayed = p.HasPlayed
            }).ToList(),
            CurrentPlayerIndex = engine.PlayerIndex,
            Turn = engine.Turn,
            Board = engine.Board,
            IsGameOver = engine.IsGameOver,
            WinnerName = winnerName,
            TilePoolCount = engine.TilePoolCount
        };
    }

    private static List<Tile> GetRackTiles(GameEngine engine, int playerIndex)
    {
        return engine.Players[playerIndex].Rack.GetAllTilesIncludingJokers().ToList();
    }

    /// <summary>
    /// Sends the current player their rack (useful after turn advancement).
    /// </summary>
    private async Task SendCurrentPlayerRack(Game game, string gameId)
    {
        var engine = game.Engine!;
        var currentPlayerIndex = engine.PlayerIndex;
        var lobbyPlayers = game.GetPlayers();

        if (currentPlayerIndex < lobbyPlayers.Count)
        {
            var connectionId = lobbyPlayers[currentPlayerIndex].ConnectionId;
            if (!string.IsNullOrEmpty(connectionId))
            {
                var rack = GetRackTiles(engine, currentPlayerIndex);
                await Clients.Client(connectionId).SendAsync(HubEvents.RackUpdated, rack);
            }
        }
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
    /// Awaits the in-flight optimal solve for the (turn, playerIndex) the caller just acted on,
    /// then sends the optimal tile list privately. Falls back to an empty list on timeout/failure.
    /// </summary>
    private async Task SendOptimalPlayToCallerAsync(
        Game game, int turnAtAction, int playerIndexAtAction, CancellationToken ct)
    {
        var pending = game.PendingOptimalSolve;
        List<Tile> tiles;

        if (pending is null
            || pending.Turn != turnAtAction
            || pending.PlayerIndex != playerIndexAtAction)
        {
            tiles = [];
        }
        else
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var result = await pending.Task.WaitAsync(linked.Token);

                tiles = result.Found
                    ? [..result.TilesToPlay,
                       ..Enumerable.Range(0, result.JokersToPlay).Select(_ => new Tile(isJoker: true))]
                    : [];
            }
            catch (OperationCanceledException)
            {
                tiles = [];
            }
            catch (Exception ex)
            {
                LogOptimalSolveFailed(ex, game.Id, turnAtAction, playerIndexAtAction);
                tiles = [];
            }
        }

        await Clients.Caller.SendAsync(HubEvents.OptimalPlay, tiles, ct);
    }

    // ========== LOGGING ==========

    [LoggerMessage(Level = LogLevel.Information, Message = "Game {GameId} started")]
    private partial void LogGameStarted(string gameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Player {PlayerName} joined game session {GameId}")]
    private partial void LogPlayerJoinedSession(string playerName, string gameId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {MethodName} for connection {ConnectionId}")]
    private partial void LogHubError(Exception ex, string methodName, string connectionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Optimal solve failed for game {GameId} turn {Turn} player {PlayerIndex}")]
    private partial void LogOptimalSolveFailed(Exception ex, string gameId, int turn, int playerIndex);
}
