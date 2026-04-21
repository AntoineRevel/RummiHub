using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using StackExchange.Redis;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public partial class GameManager(
    ILogger<GameManager> logger,
    IOptions<GameOptions> options,
    IConnectionMultiplexer redis) : IGameManager
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly ConcurrentDictionary<string, Game> _games = new();
    private readonly ConcurrentDictionary<string, string> _connectionToGameId = new();

    private static string GameKey(string id) => $"game:{id}";

    public IReadOnlyList<string> GetAvailableGameIds()
    {
        var predefined = options.Value.PredefinedIds;
        if (predefined.Count == 0) return [];
        return predefined.Where(id => !_games.ContainsKey(id)).ToList();
    }

    public Game CreateGame(string playerName, string connectionId, string? distributionSeed = null)
    {
        var game = new Game
        {
            HostConnectionId = connectionId,
            DistributionSeed = string.IsNullOrWhiteSpace(distributionSeed)
                ? Guid.NewGuid().ToString("N")[..8]
                : distributionSeed
        };

        var player = new Player
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = playerName,
            ConnectionId = connectionId
        };

        game.AddPlayer(player);
        _games[game.Id] = game;
        _connectionToGameId[connectionId] = game.Id;
        _db.JSON().Set(GameKey(game.Id), "$", GameDto.FromGame(game));

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
        _connectionToGameId[connectionId] = gameId;
        _db.JSON().Set(GameKey(gameId), "$", GameDto.FromGame(game));

        LogPlayerJoined(playerName, gameId);
        return player;
    }

    public bool RemovePlayer(string connectionId)
    {
        if (!_connectionToGameId.TryGetValue(connectionId, out var gameId))
            return false;

        if (!_games.TryGetValue(gameId, out var game))
            return false;

        if (!game.RemovePlayer(connectionId, out var removed))
            return false;

        _connectionToGameId.TryRemove(connectionId, out _);
        LogPlayerRemoved(removed!.Name, game.Id);

        if (game.PlayerCount > 0)
        {
            _db.JSON().Set(GameKey(game.Id), "$", GameDto.FromGame(game));
            return true;
        }

        _games.TryRemove(game.Id, out _);
        _db.KeyDelete(GameKey(game.Id));
        LogGameRemoved(game.Id);
        return true;
    }

    public Game? FindGameByConnection(string connectionId)
    {
        if (_connectionToGameId.TryGetValue(connectionId, out var gameId) &&
            _games.TryGetValue(gameId, out var game))
            return game;
        return null;
    }

    public bool InitializeEngine(string gameId)
    {
        if (!_games.TryGetValue(gameId, out var game))
            return false;

        var playerNames = game.GetPlayerNames();
        var engine = new GameEngine();
        engine.Initialize(playerNames, game.DistributionSeed);
        game.Engine = engine;
        game.IsStarted = true;

        _db.JSON().Set(GameKey(gameId), "$", GameDto.FromGame(game));

        LogEngineInitialized(gameId, playerNames.Count);
        return true;
    }

    public bool UpdatePlayerConnection(string gameId, string playerName, string newConnectionId)
    {
        if (!_games.TryGetValue(gameId, out var game))
            return false;

        var oldConnectionId = game.GetPlayers()
            .FirstOrDefault(p => p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
            ?.ConnectionId;

        if (!game.UpdatePlayerConnection(playerName, newConnectionId))
            return false;

        if (!string.IsNullOrEmpty(oldConnectionId) && oldConnectionId != newConnectionId)
            _connectionToGameId.TryRemove(oldConnectionId, out _);
        _connectionToGameId[newConnectionId] = gameId;

        _db.JSON().Set(GameKey(gameId), "$", GameDto.FromGame(game));
        return true;
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
