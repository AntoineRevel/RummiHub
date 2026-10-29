namespace RummiHub.Web.Models;

/// <summary>
/// When the board broadcast last stops moving on the players' screens. Each game page reports,
/// once it has drawn a broadcast board, how long its board keeps moving
/// (<c>tile-animations.js</c> knows it exactly), and the next AI move waits for the slowest
/// one instead of the server's estimate (<see cref="Rendering.BoardPacing.EstimateMs"/>),
/// which counts every move on its own and is often seconds too long. The estimate still
/// bounds the wait for reports: a page that never answers (closed tab, lost connection) holds
/// the table no longer than before.
/// </summary>
public sealed class BoardSettling(TimeProvider time)
{
    /// <summary>
    /// Longest board motion a page may report: <c>tile-animations.js</c> fast-forwards a longer
    /// backlog (<c>MAX_BACKLOG_MS</c>), and a page cannot hold the table longer than that.
    /// </summary>
    public const int MaxReportedMs = 20_000;

    private readonly Lock _lock = new();

    // Connections whose page shows this game: only they report, and a board waits for them.
    private readonly HashSet<string> _watchers = [];

    // Watchers that have not reported the current board yet.
    private readonly HashSet<string> _awaiting = [];

    private TaskCompletionSource _allReported = Completed();
    private DateTimeOffset _deadline;
    private DateTimeOffset _idleAt;
    private int _version;

    public BoardSettling() : this(TimeProvider.System) { }

    /// <summary>Numbers the boards broadcast; sent with each state so reports name their board.</summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    /// <summary>A page showing this game joined (seat checked by the caller).</summary>
    public void Watch(string connectionId)
    {
        lock (_lock) _watchers.Add(connectionId);
    }

    /// <summary>A page left: the current board stops waiting for it.</summary>
    public void Forget(string connectionId)
    {
        lock (_lock)
        {
            _watchers.Remove(connectionId);
            if (_awaiting.Remove(connectionId) && _awaiting.Count == 0) _allReported.TrySetResult();
        }
    }

    /// <summary>
    /// A new board is about to be broadcast, estimated to move for <paramref name="estimateMs"/>.
    /// Returns its version.
    /// </summary>
    public int Begin(int estimateMs)
    {
        lock (_lock)
        {
            var now = time.GetUtcNow();
            _deadline = now + TimeSpan.FromMilliseconds(Math.Max(0, estimateMs));
            _idleAt = now;
            _allReported.TrySetResult(); // a wait on the previous board is over
            _awaiting.Clear();
            _awaiting.UnionWith(_watchers);
            _allReported = _awaiting.Count == 0 ? Completed() : new(TaskCreationOptions.RunContinuationsAsynchronously);
            return ++_version;
        }
    }

    /// <summary>
    /// <paramref name="connectionId"/>'s page drew board <paramref name="version"/> and says its
    /// board keeps moving for <paramref name="busyMs"/> more. Stale boards and connections that
    /// never joined are ignored.
    /// </summary>
    public void Report(string connectionId, int version, int busyMs)
    {
        lock (_lock)
        {
            if (version != _version || !_watchers.Contains(connectionId)) return;

            var idleAt = time.GetUtcNow() + TimeSpan.FromMilliseconds(Math.Clamp(busyMs, 0, MaxReportedMs));
            if (idleAt > _idleAt) _idleAt = idleAt;
            if (_awaiting.Remove(connectionId) && _awaiting.Count == 0) _allReported.TrySetResult();
        }
    }

    /// <summary>
    /// When the current board stops moving everywhere: once every watching page has reported,
    /// the latest end they reported; past the estimate, whatever came in, never earlier than the
    /// estimate's end.
    /// </summary>
    public async Task<DateTimeOffset> IdleAtAsync(CancellationToken ct)
    {
        Task allReported;
        DateTimeOffset deadline;
        lock (_lock)
        {
            allReported = _allReported.Task;
            deadline = _deadline;
        }

        var left = deadline - time.GetUtcNow();
        if (left > TimeSpan.Zero)
        {
            try
            {
                await allReported.WaitAsync(left, time, ct);
            }
            catch (TimeoutException)
            {
                // Someone never answered: fall back on the estimate below.
            }
        }

        lock (_lock)
        {
            // A newer board replaced this one meanwhile: the caller's turn is over anyway.
            if (!ReferenceEquals(allReported, _allReported.Task)) return time.GetUtcNow();
            return _awaiting.Count == 0 ? _idleAt : (_idleAt > _deadline ? _idleAt : _deadline);
        }
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }
}
