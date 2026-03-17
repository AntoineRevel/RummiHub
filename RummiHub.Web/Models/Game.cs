namespace RummiHub.Web.Models;

public class Game
{
    private readonly Lock _playersLock = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    private List<Player> Players { get; } = [];
    public bool IsStarted { get; set; }
    public string HostConnectionId { get; init; } = string.Empty;

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
}