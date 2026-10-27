using System.Collections.Concurrent;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public partial class GameManager(ILogger<GameManager> logger, ISeedStore seedStore) : IGameManager
{
    /// <summary>
    /// How long a seed added while its game runs stays off the lobby when this server cannot
    /// reveal it at the end (restarted in between).
    /// </summary>
    public static readonly TimeSpan SeedHiddenWhilePlaying = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, Game> _games = new();

    /// <summary>Short, unique-enough identifier used for game seeds and player ids.</summary>
    private static string NewShortId() => Guid.NewGuid().ToString("N")[..8];

    public IReadOnlyList<SavedSeed> GetSeedList()
    {
        var now = DateTimeOffset.UtcNow;
        return seedStore.GetAll().Where(s => s.IsVisible(now)).ToList();
    }

    public void SaveSeed(Game game)
    {
        if (game.DistributionSeed is not { } seed)
            return;

        var running = game.Engine is not { IsGameOver: true };
        seedStore.Add(seed, running ? DateTimeOffset.UtcNow + SeedHiddenWhilePlaying : null);
        game.SeedHiddenInList = running;
        game.SeedSaved = true;
    }

    public void RevealSeed(Game game)
    {
        if (!game.SeedHiddenInList || game.DistributionSeed is not { } seed)
            return;

        game.SeedHiddenInList = false;
        seedStore.Reveal(seed);
    }

    public Game CreateGame(string playerName, string connectionId, string? distributionSeed = null)
    {
        var seed = ResolveDistributionSeed(distributionSeed, out var chosen);

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

        // A pasted seed joins the list. One picked from the list stays where it is.
        if (chosen)
        {
            if (seedStore.GetAll().Any(s => s.Seed == seed))
                game.SeedSaved = true;
            else
                SaveSeed(game);
        }

        _games[game.Id] = game;
        LogGameCreated(game.Id, playerName);
        return game;
    }

    /// <summary>
    /// Empty means "deal from a fresh CSPRNG seed". Anything else must be a well-formed seed
    /// (<see cref="DistributionSeeds.TryNormalize"/>), picked from the list or pasted.
    /// An 8-character value that is a room still held here is a game code, not a seed.
    /// </summary>
    private string ResolveDistributionSeed(string? requested, out bool chosen)
    {
        chosen = !string.IsNullOrWhiteSpace(requested);
        if (!chosen)
            return DistributionSeeds.Create();

        if (!DistributionSeeds.TryNormalize(requested, out var seed, out var error))
            throw new ArgumentException(error);

        if (seed.Length == DistributionSeeds.LegacyLength && _games.ContainsKey(seed))
            throw new ArgumentException(DistributionSeeds.GameCodePastedMessage(seed));

        return seed;
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
            RevealSeed(game);

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
        var engine = new GameEngine { GameId = game.Id };
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
}
