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
    /// Creates a game replaying a list entry: its seed, AI seats up to its table size, and the
    /// host on its seat, so the host gets the same rack and the same draws.
    /// </summary>
    Game CreateGame(string playerName, string connectionId, SavedSeed saved);

    /// <summary>
    /// Adds an AI (auto-play) seat to a not-yet-started game. Returns null if the game is
    /// missing or already started.
    /// </summary>
    Player? AddAiPlayer(string gameId);

    /// <summary>
    /// Takes the AI seat named <paramref name="name"/> off a not-yet-started game. Returns null
    /// if the game is missing or started, or has no such AI seat.
    /// </summary>
    Player? RemoveAiPlayer(string gameId, string name);

    /// <summary>The lobby seed list, most recently added first.</summary>
    IReadOnlyList<SavedSeed> GetSeedList();

    /// <summary>
    /// Adds the game's deal, as seen from <paramref name="seat"/> (seed, table size, seat), to
    /// the lobby list, at any time; it shows there at once.
    /// </summary>
    void SaveSeed(Game game, int seat, string? name);

    /// <summary>The list entry whose <see cref="SavedSeed.ListId"/> this is, or null.</summary>
    SavedSeed? FindListedSeed(string listId);

    /// <summary>Takes an entry off the lobby list by its list id (anyone may: there are no accounts).</summary>
    void RemoveSeed(string listId);

    /// <summary>Names a list entry; an empty name clears it (anyone may: there are no accounts).</summary>
    void RenameSeed(string listId, string? name);

    Game? GetGame(string gameId);

    /// <summary>Every game still in memory, in the lobby or started (admin page).</summary>
    IReadOnlyList<Game> GetGames();

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
