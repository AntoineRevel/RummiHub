using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Services;

namespace RummiHub.Web.Models;

public sealed record PendingOptimalSolve(
    int Turn,
    int PlayerIndex,
    Task<SolveResponse> Task,
    CancellationTokenSource Cts);

/// <summary>
/// A human move applied in "play &amp; review" mode: the board is already broadcast and
/// animating, but the turn does not advance until this player confirms (Next player).
/// </summary>
public sealed record PendingTurnConfirmation(int Turn, int PlayerIndex);

public class Game
{
    private readonly Lock _playersLock = new();
    private readonly Dictionary<int, Queue<DateTimeOffset>> _playAttempts = [];

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string? DistributionSeed { get; set; }

    /// <summary>
    /// The seed, once the game is over. During play it stays on the server: a seed rebuilds
    /// the whole deal, every rack and the draw order included.
    /// </summary>
    public string? RevealedDistributionSeed => Engine is { IsGameOver: true } ? DistributionSeed : null;

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
    /// Set while a "play &amp; review" move waits for its player to confirm the end of the
    /// turn. All other game actions are refused until then.
    /// Read/written only inside <see cref="ActionLock"/>.
    /// </summary>
    public PendingTurnConfirmation? PendingTurnConfirmation { get; set; }

    /// <summary>
    /// Records a play attempt by the seat at <paramref name="playerIndex"/>, or returns false
    /// when that seat already made <see cref="GameLimits.PlayAttemptsPerWindow"/> attempts within
    /// <see cref="GameLimits.PlayAttemptWindow"/>. Each attempt costs a solver round trip, and the
    /// solver serves every table. Called only inside <see cref="ActionLock"/>.
    /// </summary>
    public bool TryStartPlayAttempt(int playerIndex, DateTimeOffset now)
    {
        if (!_playAttempts.TryGetValue(playerIndex, out var attempts))
            _playAttempts[playerIndex] = attempts = new Queue<DateTimeOffset>();

        while (attempts.Count > 0 && now - attempts.Peek() >= GameLimits.PlayAttemptWindow)
            attempts.Dequeue();

        if (attempts.Count >= GameLimits.PlayAttemptsPerWindow)
            return false;

        attempts.Enqueue(now);
        return true;
    }

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

    /// <summary>
    /// Reading window (ms) the most recently broadcast move gets once it has landed, before the
    /// next AI move (0 when the board did not change). Read/written only inside
    /// <see cref="ActionLock"/>.
    /// </summary>
    public int PendingReadingMs { get; set; }

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

    /// <summary>
    /// Adds a seat while the table is under <see cref="GameLimits.MaxPlayers"/>.
    /// The check and the insert share the player lock.
    /// </summary>
    public bool AddPlayer(Player player)
    {
        lock (_playersLock)
        {
            if (Players.Count >= GameLimits.MaxPlayers)
                return false;

            Players.Add(player);
            return true;
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
    /// Attaches <paramref name="newConnectionId"/> to the human seat named
    /// <paramref name="playerName"/> when <paramref name="seatToken"/> matches the secret
    /// issued to that seat. A wrong secret leaves the current connection in place.
    /// </summary>
    public bool UpdatePlayerConnection(string playerName, string? seatToken, string newConnectionId)
    {
        lock (_playersLock)
        {
            var player = Players.FirstOrDefault(p =>
                p.Name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
            if (player == null || player.IsAuto)
                return false;
            if (!SeatSecrets.Matches(player.SeatToken, seatToken))
                return false;

            player.ConnectionId = newConnectionId;
            return true;
        }
    }
}
