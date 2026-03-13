using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using StackExchange.Redis;

namespace RummiHub.Web.Services;

/// <summary>
/// Service singleton qui s'abonne aux notifications Redis Pub/Sub
/// et dispatche les résultats aux composants Blazor en attente via TaskCompletionSource.
/// Pattern "subscribe-then-check" pour éliminer les race conditions.
/// </summary>
public sealed class SolveResultSubscriber : IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<SolveResultSubscriber> _logger;
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<SolveResponse>>> _waiters = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public SolveResultSubscriber(IConnectionMultiplexer redis, ILogger<SolveResultSubscriber> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Attend le résultat d'une résolution identifiée par son contentHash.
    /// S'abonne au channel Pub/Sub, puis vérifie si le résultat est déjà en cache (race condition safety).
    /// </summary>
    public async Task<SolveResponse> WaitForResult(string contentHash, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<SolveResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Enregistre l'annulation
        ct.Register(() => tcs.TrySetCanceled(), useSynchronizationContext: false);

        // Ajoute le TCS à la liste des waiters pour ce hash
        _waiters.AddOrUpdate(
            contentHash,
            _ => [tcs],
            (_, list) =>
            {
                lock (list) { list.Add(tcs); }
                return list;
            });

        // S'abonne au channel Pub/Sub (idempotent pour le même channel)
        var subscriber = _redis.GetSubscriber();
        await subscriber.SubscribeAsync(
            RedisChannel.Literal($"solve:done:{contentHash}"),
            (_, _) => _ = OnResultReady(contentHash));

        // Vérifie immédiatement si le résultat existe déjà (subscribe-then-check)
        var db = _redis.GetDatabase();
        var existing = await db.StringGetAsync($"solve-result:{contentHash}");
        if (!existing.IsNull)
        {
            var result = JsonSerializer.Deserialize<SolveResponse>((string)existing!, JsonOptions);
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

        var result = JsonSerializer.Deserialize<SolveResponse>((string)json!, JsonOptions);
        if (result is null) return;

        if (_waiters.TryRemove(contentHash, out var list))
        {
            lock (list)
            {
                foreach (var tcs in list)
                    tcs.TrySetResult(result);
            }
        }

        // Unsubscribe — plus personne n'attend ce hash
        var subscriber = _redis.GetSubscriber();
        await subscriber.UnsubscribeAsync(RedisChannel.Literal($"solve:done:{contentHash}"));

        _logger.LogDebug("Result dispatched for {ContentHash}", contentHash);
    }

    public void Dispose()
    {
        // Annule tous les waiters en attente
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
