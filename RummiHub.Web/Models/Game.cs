using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Services;

namespace RummiHub.Web.Models;

public sealed record PendingOptimalSolve(
    int Turn,
    int PlayerIndex,
    Task<SolveResponse> Task,
    CancellationTokenSource Cts);

public class Game
{
    private readonly Lock _playersLock = new();

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string? DistributionSeed { get; set; }
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

    /// <summary>
    /// Background solve started at the beginning of the current turn so the player
    /// can receive an "optimal play you could have made" hint after acting.
    /// Read/written only inside ActionLock.
    /// </summary>
    public PendingOptimalSolve? PendingOptimalSolve { get; set; }

    /// <summary>
    /// The board exactly as last shown to clients (after cosmetic alignment). The next board is
    /// aligned against it, and the tile-animation duration is computed from the difference.
    /// Read/written only inside <see cref="ActionLock"/>.
    /// </summary>
    public Solution? LastBroadcastBoard { get; set; }

    /// <summary>
    /// How long (ms) the client animation for the most recently broadcast transition runs. The
    /// next AI move waits this long (plus the watchable pause) so its move never overlaps the
    /// in-progress animation. Read/written only inside <see cref="ActionLock"/>.
    /// </summary>
    public int PendingAnimationMs { get; set; }

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
