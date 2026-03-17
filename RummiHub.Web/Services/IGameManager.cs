using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

public interface IGameManager
{
    Game CreateGame(string playerName, string connectionId);
    Game? GetGame(string gameId);
    Player? AddPlayer(string gameId, string playerName, string connectionId);
    bool RemovePlayer(string connectionId);
    Game? FindGameByConnection(string connectionId);
}