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

    Game? GetGame(string gameId);
    Player? AddPlayer(string gameId, string playerName, string connectionId);
    bool RemovePlayer(string connectionId);
    Game? FindGameByConnection(string connectionId);

    /// <summary>
    /// Initializes the GameEngine for a started game.
    /// </summary>
    bool InitializeEngine(string gameId);

    /// <summary>
    /// Updates a player's connection ID (for lobby → game page navigation).
    /// </summary>
    bool UpdatePlayerConnection(string gameId, string playerName, string newConnectionId);
}
