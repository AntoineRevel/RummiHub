using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public interface IGameManager
{
    /// <summary>
    /// Creates a game dealt from <paramref name="distributionSeed"/>, or from a fresh random seed
    /// when empty. Throws <see cref="ArgumentException"/> (message for the player) on a malformed seed.
    /// </summary>
    Game CreateGame(string playerName, string connectionId, string? distributionSeed = null);

    /// <summary>
    /// Adds an AI (auto-play) seat to a not-yet-started game. Returns null if the game is
    /// missing or already started.
    /// </summary>
    Player? AddAiPlayer(string gameId);

    /// <summary>The lobby seed list, most recently added first.</summary>
    IReadOnlyList<SavedSeed> GetSeedList();

    /// <summary>Adds the game's seed to the lobby list, at any time; it shows there at once.</summary>
    void SaveSeed(Game game);

    /// <summary>The listed seed whose <see cref="DistributionSeeds.ListId"/> this is, or null.</summary>
    string? FindListedSeed(string listId);

    /// <summary>Takes a seed off the lobby list by its list id (anyone may: there are no accounts).</summary>
    void RemoveSeed(string listId);

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
