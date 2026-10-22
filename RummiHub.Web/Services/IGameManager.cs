using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public interface IGameManager
{
    Game CreateGame(string playerName, string connectionId, string? distributionSeed = null);

    /// <summary>
    /// Adds an AI (auto-play) seat to a not-yet-started game. Returns null if the game is
    /// missing or already started.
    /// </summary>
    Player? AddAiPlayer(string gameId);

    IReadOnlyList<string> GetAvailableGameIds();

    /// <summary>
    /// Saves a distribution seed to the lobby seed list so players can replay the same
    /// game later. Returns true if it was newly added (false if blank or already present).
    /// </summary>
    bool AddSeed(string seed);

    /// <summary>
    /// True when <paramref name="seed"/> is a safe token already known to the server
    /// (configured replay list or a seed saved from a game this process dealt).
    /// </summary>
    bool IsKnownDistributionSeed(string? seed);

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
