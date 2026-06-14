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
    // Garde-fou : Pub/Sub Redis est fire-and-forget. Une notification perdue
    // (connexion coupée, worker mort) ne doit pas suspendre la partie indéfiniment.
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CachePollInterval = TimeSpan.FromSeconds(5);

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
    /// The cache is then re-checked periodically as a fallback for lost Pub/Sub
    /// notifications, and the wait is capped by <see cref="ResultTimeout"/>.
    /// </summary>
    /// <exception cref="TimeoutException">No result within <see cref="ResultTimeout"/>.</exception>
    public async Task<SolveResponse> WaitForResult(string contentHash, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SolveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

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
            RedisChannel.Literal(RedisKeys.SolveDoneChannel(contentHash)),
            (_, _) => _ = OnResultReady(contentHash));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResultTimeout);

            // Subscribe-then-check, puis re-vérification périodique du cache :
            // couvre la notification Pub/Sub perdue en plus de la race initiale.
            while (true)
            {
                var cached = await TryReadResult(contentHash);
                if (cached is not null)
                    return cached;

                var delay = Task.Delay(CachePollInterval, timeout.Token);
                var completed = await Task.WhenAny(tcs.Task, delay);
                if (completed == tcs.Task)
                    return await tcs.Task;

                await delay; // propage l'annulation (ct appelant ou timeout)
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No solve result received for {contentHash} within {ResultTimeout.TotalSeconds:F0}s.");
        }
        finally
        {
            await RemoveWaiter(contentHash, tcs);
        }
    }

    private async Task<SolveResponse?> TryReadResult(string contentHash)
    {
        var db = _redis.GetDatabase();
        var json = await db.ExecuteAsync("JSON.GET", RedisKeys.SolveResult(contentHash));
        if (json.IsNull) return null;

        return JsonSerializer.Deserialize<SolveResponse>(json.ToString()!, RummiJsonOptions.Default);
    }

    /// <summary>
    /// Detaches a waiter once it has completed (result, cancellation or timeout),
    /// and unsubscribes from the Pub/Sub channel when it was the last one —
    /// including on the cache-hit path, which previously leaked the subscription.
    /// </summary>
    private async Task RemoveWaiter(string contentHash, TaskCompletionSource<SolveResponse> tcs)
    {
        if (!_waiters.TryGetValue(contentHash, out var list))
            return; // OnResultReady already removed the entry and unsubscribed

        bool empty;
        lock (list)
        {
            list.Remove(tcs);
            empty = list.Count == 0;
        }

        if (empty && _waiters.TryRemove(new KeyValuePair<string, List<TaskCompletionSource<SolveResponse>>>(contentHash, list)))
        {
            await _redis.GetSubscriber()
                .UnsubscribeAsync(RedisChannel.Literal(RedisKeys.SolveDoneChannel(contentHash)));
        }
    }

    private async Task OnResultReady(string contentHash)
    {
        var result = await TryReadResult(contentHash);
        if (result is null)
        {
            _logger.LogWarning("Pub/Sub notification for {ContentHash} but no result in cache", contentHash);
            return;
        }

        if (_waiters.TryRemove(contentHash, out var list))
        {
            lock (list)
            {
                foreach (var tcs in list)
                    tcs.TrySetResult(result);
            }
        }

        var subscriber = _redis.GetSubscriber();
        await subscriber.UnsubscribeAsync(RedisChannel.Literal(RedisKeys.SolveDoneChannel(contentHash)));

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
