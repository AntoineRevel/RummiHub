using Microsoft.AspNetCore.SignalR;
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
                selectedTiles, client, solveResultSubscriber, CancellationToken.None);

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

                    // Advance turn
                    game.Engine.AdvanceToNextPlayer();

                    // Broadcast new game state to all
                    var state = BuildGameState(game, game.Engine.Players[playerIndex.Value].TilesToPlay);
                    await Clients.Group(gameId).SendAsync(HubEvents.GameStateUpdated, state);

                    // Send the next player their rack (in case they just connected and need it)
                    await SendCurrentPlayerRack(game, gameId);
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

            // Advance turn
            game.Engine.AdvanceToNextPlayer();

            // Broadcast state to all
            var state = BuildGameState(game);
            await Clients.Group(gameId).SendAsync(HubEvents.GameStateUpdated, state);

            // Send next player their rack
            await SendCurrentPlayerRack(game, gameId);
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

    private static GameStateDto BuildGameState(Game game, List<Tile>? lastPlayedTiles = null)
    {
        var engine = game.Engine!;
        var winnerName = engine.IsGameOver ? engine.CurrentPlayer.Name : null;

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
            LastPlayedTiles = lastPlayedTiles,
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

    // ========== LOGGING ==========

    [LoggerMessage(Level = LogLevel.Information, Message = "Game {GameId} started")]
    private partial void LogGameStarted(string gameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Player {PlayerName} joined game session {GameId}")]
    private partial void LogPlayerJoinedSession(string playerName, string gameId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {MethodName} for connection {ConnectionId}")]
    private partial void LogHubError(Exception ex, string methodName, string connectionId);
}
