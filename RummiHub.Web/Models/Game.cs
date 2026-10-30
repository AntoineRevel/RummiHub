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
    /// Set when the game was created from a lobby list entry: the table size and seat it was
    /// saved with. The host keeps that seat (<see cref="KeepHostSeat"/>) so they get the same rack.
    /// </summary>
    public SavedSeed? SavedFrom { get; init; }

    private readonly HashSet<int> _savedSeats = [];

    private List<Player> Players { get; } = [];
    public bool IsStarted { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Set when the host starts the game; null in the lobby.</summary>
    public DateTimeOffset? StartedAt { get; set; }
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
    /// Background solve started at the beginning of the current turn. A human pulls
    /// its tiles into the play zone with the Hint button; an AI seat plays it.
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
    /// Estimated duration (ms) of the client animation for the most recently broadcast
    /// transition: the longest the next AI move waits for the players' pages to report it over
    /// (<see cref="BoardSettling"/>), and how long its highlight lasts. Read/written only inside
    /// <see cref="ActionLock"/>.
    /// </summary>
    public int PendingAnimationMs { get; set; }

    /// <summary>
    /// Reading window (ms) the most recently broadcast move gets once it has landed, before the
    /// next AI move (0 when the board did not change). Read/written only inside
    /// <see cref="ActionLock"/>.
    /// </summary>
    public int PendingReadingMs { get; set; }

    /// <summary>
    /// When the last broadcast board stops moving on the players' screens, as their pages
    /// report it; the next AI move waits for that rather than <see cref="PendingAnimationMs"/>.
    /// </summary>
    public BoardSettling BoardSettling { get; } = new();

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

    /// <summary>The waiting room's list: names in seat order, AI seats flagged.</summary>
    public List<LobbyPlayerDto> GetLobbyPlayers()
    {
        lock (_playersLock)
        {
            return Players.Select(p => new LobbyPlayerDto(p.Name, p.IsAuto)).ToList();
        }
    }

    /// <summary>The seats whose deal (seed, table size, seat) is on the lobby list.</summary>
    public List<int> GetSavedSeats()
    {
        lock (_savedSeats)
        {
            return _savedSeats.Order().ToList();
        }
    }

    public bool IsSeedSaved(int seat)
    {
        lock (_savedSeats)
        {
            return _savedSeats.Contains(seat);
        }
    }

    public void MarkSeedSaved(int seat)
    {
        lock (_savedSeats)
        {
            _savedSeats.Add(seat);
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
            KeepHostSeat();
            return true;
        }
    }

    /// <summary>Takes the AI seat named <paramref name="name"/> off a game still in the lobby.</summary>
    public bool RemoveAutoPlayer(string name, out Player? removed)
    {
        lock (_playersLock)
        {
            removed = IsStarted
                ? null
                : Players.FirstOrDefault(p => p.IsAuto && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed == null) return false;
            Players.Remove(removed);
            KeepHostSeat();
            return true;
        }
    }

    /// <summary>
    /// In a game created from a list entry, puts the host back on the seat it was saved from
    /// (or the last seat while the table is smaller), whoever joins or leaves; the others keep
    /// their order. A seed deals each seat the same rack whatever the table size.
    /// Called under the player lock.
    /// </summary>
    private void KeepHostSeat()
    {
        if (SavedFrom is null || IsStarted)
            return;

        var host = Players.FirstOrDefault(p => p.ConnectionId == HostConnectionId);
        if (host is null)
            return;

        Players.Remove(host);
        Players.Insert(Math.Min(SavedFrom.Seat, Players.Count), host);
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
            KeepHostSeat();
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
