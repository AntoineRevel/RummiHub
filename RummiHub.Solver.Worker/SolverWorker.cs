using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using StackExchange.Redis;

namespace RummiHub.Solver.Worker;

public class SolverWorker(IConnectionMultiplexer redis, ILogger<SolverWorker> logger) : BackgroundService
{
    private const string StreamKey = "solve-stream";
    private const string GroupName = "solvers";
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(1);

    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid().ToString()[..8]}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[{WorkerId}] Solver worker started", _workerId);
        var db = redis.GetDatabase();

        // Crée le consumer group s'il n'existe pas encore
        try
        {
            await db.StreamCreateConsumerGroupAsync(StreamKey, GroupName, "0-0", createStream: true);
            logger.LogInformation("Consumer group '{GroupName}' created on stream '{StreamKey}'", GroupName, StreamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Le groupe existe déjà — c'est normal avec plusieurs replicas
        }

        // Phase 1 : récupère les messages pending (non ACK après un crash)
        await ProcessPendingMessages(db, stoppingToken);

        // Phase 2 : boucle principale — lit les nouveaux messages
        using var idlePollTimer = new PeriodicTimer(IdlePollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            var entries = await db.StreamReadGroupAsync(
                StreamKey, GroupName, _workerId,
                count: 1);

            if (entries.Length == 0)
            {
                // Pas de nouveau message — attend avant de réessayer
                // (StackExchange.Redis ne supporte pas BLOCK sur XREADGROUP)
                await idlePollTimer.WaitForNextTickAsync(stoppingToken);
                continue;
            }

            await ProcessEntry(db, entries[0], stoppingToken);
        }
    }

    private async Task ProcessPendingMessages(IDatabase db, CancellationToken ct)
    {
        // Lit les messages délivrés à ce consumer mais non ACK (après un crash)
        var pending = await db.StreamReadGroupAsync(
            StreamKey, GroupName, _workerId,
            count: 10, position: "0-0");

        if (pending.Length > 0)
        {
            logger.LogInformation("[{WorkerId}] Recovering {Count} pending message(s)", _workerId, pending.Length);
            foreach (var entry in pending)
            {
                if (ct.IsCancellationRequested) break;
                await ProcessEntry(db, entry, ct);
            }
        }
    }

    private async Task ProcessEntry(IDatabase db, StreamEntry entry, CancellationToken ct)
    {
        var contentHash = entry["contentHash"].ToString();
        var payload = entry["payload"].ToString();

        var request = JsonSerializer.Deserialize<SolveRequest>(payload, JsonOptions);
        if (request is null)
        {
            await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);
            return;
        }

        logger.LogInformation("[{WorkerId}] Processing {ContentHash} (request {RequestId})",
            _workerId, contentHash, request.RequestId);

        var response = await Solve(request, _workerId, ct);
        var resultJson = JsonSerializer.Serialize(response, JsonOptions);

        // 1. Stocke le résultat (1h TTL)
        await db.StringSetAsync($"solve-result:{contentHash}", resultJson, TimeSpan.FromHours(1));

        // 2. Publie la notification pour les subscribers (Blazor Server)
        var subscriber = redis.GetSubscriber();
        await subscriber.PublishAsync(RedisChannel.Literal($"solve:done:{contentHash}"), "done");

        // 3. ACK le message dans le stream
        await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);

        // 4. Libère le verrou de déduplication
        await db.KeyDeleteAsync($"solve-lock:{contentHash}");

        logger.LogInformation("[{WorkerId}] {ContentHash} done. Solved={Solved}",
            _workerId, contentHash, response.Solved);
    }

    private static async Task<SolveResponse> Solve(SolveRequest request, string workerId, CancellationToken ct = default)
    {
        var delay = Random.Shared.Next(1000, 6000);
        await Task.Delay(delay, ct);

        return new SolveResponse
        {
            RequestId = request.RequestId,
            Solved = false,
            NewBoard = request.Board,
            RemainingHand = request.Hand,
            WorkerId = workerId,
            Error = $"Solver not yet implemented (simulated {delay}ms)"
        };
    }
}
