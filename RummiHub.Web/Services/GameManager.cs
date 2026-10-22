using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public partial class GameManager(ILogger<GameManager> logger, IOptions<GameOptions> options, ISeedStore seedStore) : IGameManager
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
            .Select(id => id?.Trim() ?? string.Empty)
            .Where(DistributionSeeds.IsSafe)
            .Distinct()
            .Where(id => !_games.ContainsKey(id))
            .ToList();
    }

    public bool IsKnownDistributionSeed(string? seed)
    {
        seed = seed?.Trim() ?? string.Empty;
        if (!DistributionSeeds.IsSafe(seed))
            return false;

        return options.Value.PredefinedIds.Any(id => string.Equals(id?.Trim(), seed, StringComparison.Ordinal))
               || _savedSeeds.ContainsKey(seed);
    }

    public bool AddSeed(string seed)
    {
        seed = seed?.Trim() ?? string.Empty;
        if (!DistributionSeeds.IsSafe(seed))
            return false;

        if (!_savedSeeds.TryAdd(seed, 0))
            return false;

        // File persistence is Development-only; production keeps the seed in memory.
        seedStore.Persist(seed);

        LogSeedSaved(seed);
        return true;
    }

    public Game CreateGame(string playerName, string connectionId, string? distributionSeed = null)
    {
        var seed = ResolveDistributionSeed(distributionSeed);

        var game = new Game
        {
            HostConnectionId = connectionId,
            DistributionSeed = seed
        };

        var player = new Player
        {
            Id = NewShortId(),
            Name = playerName,
            ConnectionId = connectionId,
            SeatToken = SeatSecrets.Create()
        };

        if (!game.AddPlayer(player))
            throw new InvalidOperationException("Could not seat the host.");

        _games[game.Id] = game;
        LogGameCreated(game.Id, playerName);
        return game;
    }

    /// <summary>
    /// Empty means "deal from a fresh CSPRNG seed". Anything else must already be on the
    /// server list; a client-invented seed is refused before a game is stored.
    /// </summary>
    private string ResolveDistributionSeed(string? requested)
    {
        requested = requested?.Trim() ?? string.Empty;
        if (requested.Length == 0)
            return DistributionSeeds.Create();

        if (!IsKnownDistributionSeed(requested))
            throw new ArgumentException("Distribution seed is not in the server list.");

        return requested;
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
            IsAuto = true,
            SeatToken = SeatSecrets.Create()
        };

        if (!game.AddPlayer(player))
            return null;

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
            ConnectionId = connectionId,
            SeatToken = SeatSecrets.Create()
        };

        if (!game.AddPlayer(player))
            return null;

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
        if (players.Count is < GameLimits.MinPlayers or > GameLimits.MaxPlayers)
            return false;

        var playerNames = players.Select(p => p.Name).ToList();
        var autoPlayers = players.Select(p => p.IsAuto).ToList();
        var engine = new GameEngine();
        engine.Initialize(playerNames, game.DistributionSeed, autoPlayers);
        game.Engine = engine;

        LogEngineInitialized(gameId, playerNames.Count);
        return true;
    }

    public bool UpdatePlayerConnection(string gameId, string playerName, string? seatToken, string newConnectionId)
    {
        if (!_games.TryGetValue(gameId, out var game))
            return false;

        return game.UpdatePlayerConnection(playerName, seatToken, newConnectionId);
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
