using System.Collections.Concurrent;
using System.Text.Json;
using RummiHub.Core.Contracts;
using StackExchange.Redis;

namespace RummiHub.Web.Services;

/// <summary>
/// Singleton service that subscribes to Redis Pub/Sub notifications
/// and dispatches results to waiting Blazor components via TaskCompletionSource.
/// Uses "subscribe-then-check" pattern to eliminate race conditions.
/// </summary>
public sealed class SolveResultSubscriber : IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SolveResultSubscriber> _logger;
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<SolveResponse>>> _waiters = new();

    public SolveResultSubscriber(IConnectionMultiplexer redis, ILogger<SolveResultSubscriber> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Waits for a solve result identified by its contentHash.
    /// Subscribes to Pub/Sub channel, then checks cache (race condition safety).
    /// </summary>
    public async Task<SolveResponse> WaitForResult(string contentHash, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SolveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        ct.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

        _waiters.AddOrUpdate(
            contentHash,
            _ => [tcs],
            (_, list) =>
            {
                lock (list) { list.Add(tcs); }
                return list;
            });

        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(
            RedisChannel.Literal($"solve:done:{contentHash}"),
            (_, _) => _ = OnResultReady(contentHash));

        // Subscribe-then-check: verify cache immediately after subscribing
        var db = _redis.GetDatabase();
        var existing = await db.StringGetAsync($"solve-result:{contentHash}");
        if (!existing.IsNull)
        {
            var result = JsonSerializer.Deserialize<SolveResponse>((string)existing!, RummiJsonOptions.Default);
            if (result is not null)
            {
                tcs.TrySetResult(result);
            }
        }

        return await tcs.Task;
    }

    private async Task OnResultReady(string contentHash)
    {
        var db = _redis.GetDatabase();
        var json = await db.StringGetAsync($"solve-result:{contentHash}");

        if (json.IsNull)
        {
            _logger.LogWarning("Pub/Sub notification for {ContentHash} but no result in cache", contentHash);
            return;
        }

        var result = JsonSerializer.Deserialize<SolveResponse>((string)json!, RummiJsonOptions.Default);
        if (result is null) return;

        if (_waiters.TryRemove(contentHash, out var list))
        {
            lock (list)
            {
                foreach (var tcs in list)
                    tcs.TrySetResult(result);
            }
        }

        var subscriber = _redis.GetSubscriber();
        await subscriber.UnsubscribeAsync(RedisChannel.Literal($"solve:done:{contentHash}"));

        _logger.LogDebug("Result dispatched for {ContentHash}", contentHash);
    }

    public void Dispose()
    {
        foreach (var (_, list) in _waiters)
        {
            lock (list)
            {
                foreach (var tcs in list)
                    tcs.TrySetCanceled();
            }
        }
        _waiters.Clear();
    }
}
