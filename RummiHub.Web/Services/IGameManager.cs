using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public interface IGameManager
{
    Task<Game> CreateGame(string playerName, string connectionId, string? distributionSeed = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds an AI (auto-play) seat to a not-yet-started game. Returns null if the game is
    /// missing or already started.
    /// </summary>
    Player? AddAiPlayer(string gameId);

    /// <summary>Proposed distribution seeds, newest first.</summary>
    Task<IReadOnlyList<string>> GetProposedSeedsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a distribution seed to the shared list. The value must be a 64-character
    /// seed (or an older seed that is already listed).
    /// </summary>
    Task<SeedAddResult> AddSeedAsync(string seed, CancellationToken cancellationToken = default);

    Game? GetGame(string gameId);

    /// <summary>
    /// Adds a human seat. Returns null when the game is missing, already started, or already
    /// has <see cref="GameLimits.MaxPlayers"/> seats.
    /// </summary>
    Player? AddPlayer(string gameId, string playerName, string connectionId);
    bool RemovePlayer(string connectionId);
    Game? FindGameByConnection(string connectionId);

    /// <summary>
    /// Initializes the GameEngine for a started game.
    /// </summary>
    bool InitializeEngine(string gameId);

    /// <summary>
    /// Attaches a new connection to an existing human seat. Requires the seat secret issued
    /// when that player created or joined the game.
    /// </summary>
    bool UpdatePlayerConnection(string gameId, string playerName, string? seatToken, string newConnectionId);
}
