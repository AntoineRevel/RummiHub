using RummiHub.Web.Services;

namespace RummiHub.Web.Models;

public class Game
{
    private readonly Lock _playersLock = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    private List<Player> Players { get; } = [];
    public bool IsStarted { get; set; }
    public string HostConnectionId { get; init; } = string.Empty;

    /// <summary>
    /// Server-side game engine, initialized when the game starts.
    /// </summary>
    public GameEngine? Engine { get; set; }

    /// <summary>
    /// Serializes all game-mutating hub calls for this game.
    /// </summary>
    public SemaphoreSlim ActionLock { get; } = new(1, 1);

    public int PlayerCount
    {
        get
        {
            lock (_playersLock)
            {
                return Players.Count;
            }
        }
    }

    public List<string> GetPlayerNames()
    {
        lock (_playersLock)
        {
            return Players.Select(p => p.Name).ToList();
        }
    }

    public List<Player> GetPlayers()
    {
        lock (_playersLock)
        {
            return [..Players];
        }
    }

    public bool HasPlayerWithName(string name)
    {
        lock (_playersLock)
        {
            return Players.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void AddPlayer(Player player)
    {
        lock (_playersLock)
        {
            Players.Add(player);
        }
    }

    public bool HasConnection(string connectionId)
    {
        lock (_playersLock)
        {
            return Players.Any(p => p.ConnectionId == connectionId);
        }
    }

    public bool RemovePlayer(string connectionId, out Player? removed)
    {
        lock (_playersLock)
        {
            removed = Players.FirstOrDefault(p => p.ConnectionId == connectionId);
            if (removed == null) return false;
            Players.Remove(removed);
            return true;
        }
    }

    /// <summary>
    /// Finds a player's engine index by their SignalR connection ID.
    /// Returns null if not found.
    /// </summary>
    public int? FindPlayerIndexByConnection(string connectionId)
    {
        lock (_playersLock)
        {
            var idx = Players.FindIndex(p => p.ConnectionId == connectionId);
            return idx >= 0 ? idx : null;
        }
    }

    /// <summary>
    /// Updates a player's connection ID (used when navigating from lobby to game page).
    /// Returns true if the player was found and updated.
    /// </summary>
    public bool UpdatePlayerConnection(string playerName, string newConnectionId)
    {
        lock (_playersLock)
        {
            var player = Players.FirstOrDefault(p =>
                p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
            if (player == null) return false;
            player.ConnectionId = newConnectionId;
            return true;
        }
    }
}
