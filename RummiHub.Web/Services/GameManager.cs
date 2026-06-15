using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public partial class GameManager(ILogger<GameManager> logger, IOptions<GameOptions> options) : IGameManager
{
    private readonly ConcurrentDictionary<string, Game> _games = new();

    /// <summary>Distribution seeds saved at runtime (e.g. from the in-game "replay" button).</summary>
    private readonly ConcurrentDictionary<string, byte> _savedSeeds = new();

    /// <summary>Short, unique-enough identifier used for game seeds and player ids.</summary>
    private static string NewShortId() => Guid.NewGuid().ToString("N")[..8];

    public IReadOnlyList<string> GetAvailableGameIds()
    {
        return options.Value.PredefinedIds
            .Concat(_savedSeeds.Keys)
            .Distinct()
            .Where(id => !_games.ContainsKey(id))
            .ToList();
    }

    public bool AddSeed(string seed)
    {
        seed = seed?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(seed))
            return false;

        if (!_savedSeeds.TryAdd(seed, 0))
            return false;

        LogSeedSaved(seed);
        return true;
    }

    public Game CreateGame(string playerName, string connectionId, string? distributionSeed = null)
    {
        var game = new Game
        {
            HostConnectionId = connectionId,
            DistributionSeed = string.IsNullOrWhiteSpace(distributionSeed)
                ? NewShortId()
                : distributionSeed
        };

        var player = new Player
        {
            Id = NewShortId(),
            Name = playerName,
            ConnectionId = connectionId
        };

        game.AddPlayer(player);
        _games[game.Id] = game;
        LogGameCreated(game.Id, playerName);
        return game;
    }

    /// <summary>
    /// Adds an AI (auto-play) seat to a not-yet-started game, with a unique generated name.
    /// AI seats have no connection — they are driven by the solver. Returns null if the game
    /// is missing or already started.
    /// </summary>
    public Player? AddAiPlayer(string gameId)
    {
        if (!_games.TryGetValue(gameId, out var game) || game.IsStarted)
            return null;

        var n = 1;
        string name;
        do { name = $"AI {n++}"; } while (game.HasPlayerWithName(name));

        var player = new Player
        {
            Id = NewShortId(),
            Name = name,
            IsAuto = true
        };

        game.AddPlayer(player);
        LogPlayerJoined(name, gameId);
        return player;
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
            Id = NewShortId(),
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

        var players = game.GetPlayers();
        var playerNames = players.Select(p => p.Name).ToList();
        var autoPlayers = players.Select(p => p.IsAuto).ToList();
        var engine = new GameEngine();
        engine.Initialize(playerNames, game.DistributionSeed, autoPlayers);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed {Seed} saved to the lobby list")]
    private partial void LogSeedSaved(string seed);
}
