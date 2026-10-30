using System.Collections.Concurrent;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public partial class GameManager(ILogger<GameManager> logger, ISeedStore seedStore) : IGameManager
{
    private readonly ConcurrentDictionary<string, Game> _games = new();

    /// <summary>Short, unique-enough identifier used for game seeds and player ids.</summary>
    private static string NewShortId() => Guid.NewGuid().ToString("N")[..8];

    public IReadOnlyList<SavedSeed> GetSeedList() => seedStore.GetAll();

    public SavedSeed? FindListedSeed(string listId) =>
        seedStore.GetAll().FirstOrDefault(s => s.ListId == listId);

    public void RemoveSeed(string listId) => seedStore.Remove(listId);

    public void RenameSeed(string listId, string? name) => seedStore.Rename(listId, name);

    public void SaveSeed(Game game, int seat, string? name)
    {
        if (game.DistributionSeed is not { } seed || game.Engine is not { } engine)
            return;
        if (seat < 0 || seat >= engine.Players.Count)
            return;

        seedStore.Add(seed, engine.Players.Count, seat, name);
        game.MarkSeedSaved(seat);
    }

    public Game CreateGame(string playerName, string connectionId, string? distributionSeed = null)
    {
        var seed = ResolveDistributionSeed(distributionSeed);
        return CreateGame(playerName, connectionId, new Game
        {
            HostConnectionId = connectionId,
            DistributionSeed = seed
        });
    }

    public Game CreateGame(string playerName, string connectionId, SavedSeed saved)
    {
        var game = CreateGame(playerName, connectionId, new Game
        {
            HostConnectionId = connectionId,
            DistributionSeed = saved.Seed,
            SavedFrom = saved
        });

        // The table as it was saved: AI seats around the host, who sits on the saved seat.
        // The host removes an AI to make room for a friend.
        for (var i = 1; i < saved.Players; i++)
            AddAiPlayer(game.Id);
        return game;
    }

    private Game CreateGame(string playerName, string connectionId, Game game)
    {
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
    /// Empty means "deal from a fresh CSPRNG seed". Anything else must be a well-formed pasted
    /// seed (<see cref="DistributionSeeds.TryNormalize"/>).
    /// </summary>
    private static string ResolveDistributionSeed(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return DistributionSeeds.Create();

        if (!DistributionSeeds.TryNormalize(requested, out var seed, out var error))
            throw new ArgumentException(error);

        return seed;
    }

    /// <summary>
    /// Adds an AI (auto-play) seat to a not-yet-started game, with a unique generated name.
    /// AI seats have no connection — they are driven by the solver. Returns null if the game
    /// is missing or already started.
    /// </summary>
    public Player? RemoveAiPlayer(string gameId, string name)
    {
        if (!_games.TryGetValue(gameId, out var game) || !game.RemoveAutoPlayer(name, out var removed))
            return null;

        LogPlayerRemoved(removed!.Name, gameId);
        return removed;
    }

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

    public IReadOnlyList<Game> GetGames() => [.._games.Values];

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
        var engine = new GameEngine { GameId = game.Id };
        engine.Initialize(playerNames, game.DistributionSeed, autoPlayers);
        game.Engine = engine;

        // Replaying a list entry at its table size: the host's seat holds that entry.
        if (game.SavedFrom is { } saved && saved.Players == players.Count)
            game.MarkSeedSaved(saved.Seat);

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
