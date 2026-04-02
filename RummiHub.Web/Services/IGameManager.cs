using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public interface IGameManager
{
    Game CreateGame(string playerName, string connectionId, string? distributionSeed = null);
    IReadOnlyList<string> GetAvailableGameIds();
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
