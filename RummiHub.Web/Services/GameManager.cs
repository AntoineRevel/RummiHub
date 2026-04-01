using System.Collections.Concurrent;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public partial class GameManager(ILogger<GameManager> logger) : IGameManager
{
    private readonly ConcurrentDictionary<string, Game> _games = new();

    public Game CreateGame(string playerName, string connectionId)
    {
        var game = new Game
        {
            HostConnectionId = connectionId
        };

        var player = new Player
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = playerName,
            ConnectionId = connectionId
        };

        game.AddPlayer(player);
        _games[game.Id] = game;
        LogGameCreated(game.Id, playerName);
        return game;
    }

    public Game? GetGame(string gameId)
    {
        _games.TryGetValue(gameId, out var game);
        return game;
    }

    public Player? AddPlayer(string gameId, string playerName, string connectionId)
    {
        if (!_games.TryGetValue(gameId, out var game))
            return null;

        if (game.IsStarted)
            return null;

        var player = new Player
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = playerName,
            ConnectionId = connectionId
        };

        game.AddPlayer(player);
        LogPlayerJoined(playerName, gameId);
        return player;
    }

    public bool RemovePlayer(string connectionId)
    {
        foreach (var game in _games.Values)
        {
            if (!game.RemovePlayer(connectionId, out var removed)) continue;

            LogPlayerRemoved(removed!.Name, game.Id);

            if (game.PlayerCount != 0) return true;

            _games.TryRemove(game.Id, out _);
            LogGameRemoved(game.Id);

            return true;
        }

        return false;
    }

    public Game? FindGameByConnection(string connectionId)
    {
        return _games.Values.FirstOrDefault(g => g.HasConnection(connectionId));
    }

    public bool InitializeEngine(string gameId)
    {
        if (!_games.TryGetValue(gameId, out var game))
            return false;

        var playerNames = game.GetPlayerNames();
        var engine = new GameEngine();
        engine.Initialize(playerNames);
        game.Engine = engine;

        LogEngineInitialized(gameId, playerNames.Count);
        return true;
    }

    public bool UpdatePlayerConnection(string gameId, string playerName, string newConnectionId)
    {
        if (!_games.TryGetValue(gameId, out var game))
            return false;

        return game.UpdatePlayerConnection(playerName, newConnectionId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Game {GameId} created by {PlayerName}")]
    private partial void LogGameCreated(string gameId, string playerName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Player {PlayerName} joined game {GameId}")]
    private partial void LogPlayerJoined(string playerName, string gameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Player {PlayerName} removed from game {GameId}")]
    private partial void LogPlayerRemoved(string playerName, string gameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Game {GameId} removed (no players left)")]
    private partial void LogGameRemoved(string gameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine initialized for game {GameId} with {PlayerCount} players")]
    private partial void LogEngineInitialized(string gameId, int playerCount);
}
