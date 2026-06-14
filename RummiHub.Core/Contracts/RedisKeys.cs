namespace RummiHub.Core.Contracts;

/// <summary>
/// Single source of truth for the Redis key and Pub/Sub channel conventions of the
/// solve pipeline, shared by the API (producer), the Worker (consumer) and the Web
/// (waiter). Renaming a convention here updates all three services at once.
///
/// Channels are exposed as plain strings so Core stays free of a StackExchange.Redis
/// dependency; callers wrap them with <c>RedisChannel.Literal(...)</c>.
/// </summary>
public static class RedisKeys
{
    /// <summary>Redis Stream carrying solve requests, read by the <see cref="SolversGroup"/> group.</summary>
    public const string SolveStream = "solve-stream";

    /// <summary>
    /// High-priority Redis Stream drained before <see cref="SolveStream"/>. Carries
    /// move-validation requests so they jump ahead of background optimal-play hints.
    /// Read by the same <see cref="SolversGroup"/> group.
    /// </summary>
    public const string SolvePriorityStream = "solve-stream-priority";

    /// <summary>Consumer group name reading <see cref="SolveStream"/> and <see cref="SolvePriorityStream"/>.</summary>
    public const string SolversGroup = "solvers";

    /// <summary>RedisJSON key holding the cached <c>SolveResponse</c> for a content hash.</summary>
    public static string SolveResult(string contentHash) => $"solve-result:{contentHash}";

    /// <summary>Deduplication lock (SET NX) preventing duplicate enqueues for a content hash.</summary>
    public static string SolveLock(string contentHash) => $"solve-lock:{contentHash}";

    /// <summary>Pub/Sub channel that wakes idle workers when a new entry is enqueued.</summary>
    public const string SolveEnqueueChannel = "solve:enqueue";

    /// <summary>Pub/Sub channel notifying waiters that a content hash's result is ready.</summary>
    public static string SolveDoneChannel(string contentHash) => $"solve:done:{contentHash}";
}
