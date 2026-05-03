using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Strategies;
using StackExchange.Redis;

namespace RummiHub.Solver.Worker;

public class SolverWorker(IConnectionMultiplexer redis, ILogger<SolverWorker> logger) : BackgroundService
{
    private const string StreamKey = "solve-stream";
    private const string GroupName = "solvers";
    private static readonly RedisChannel EnqueueChannel = RedisChannel.Literal(RedisChannels.SolveEnqueue);

    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid().ToString()[..8]}";

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
        var subscriber = redis.GetSubscriber();
        var wakeSignal = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        await subscriber.SubscribeAsync(EnqueueChannel, (_, _) =>
        {
            wakeSignal.Writer.TryWrite(true);
        });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var entries = await db.StreamReadGroupAsync(
                    StreamKey, GroupName, _workerId,
                    count: 1);

                if (entries.Length == 0)
                {
                    await wakeSignal.Reader.ReadAsync(stoppingToken);
                    continue;
                }

                await ProcessEntry(db, entries[0], stoppingToken);
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(EnqueueChannel);
            wakeSignal.Writer.TryComplete();
        }
    }

    private async Task ProcessPendingMessages(IDatabase db, CancellationToken ct)
    {
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

        var request = JsonSerializer.Deserialize<SolveRequest>(payload, RummiJsonOptions.Default);
        if (request is null)
        {
            await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);
            return;
        }

        logger.LogInformation("[{WorkerId}] Processing {ContentHash} (request {RequestId})",
            _workerId, contentHash, request.RequestId);

        var response = await Solve(request, ct);
        var resultJson = JsonSerializer.Serialize(response, RummiJsonOptions.Default);

        // 1. Stocke le résultat en JSON natif (1h TTL)
        await db.ExecuteAsync("JSON.SET", $"solve-result:{contentHash}", "$", resultJson);
        await db.KeyExpireAsync($"solve-result:{contentHash}", TimeSpan.FromHours(1));

        // 2. Publie la notification pour les subscribers (Blazor Server)
        var subscriber = redis.GetSubscriber();
        await subscriber.PublishAsync(RedisChannel.Literal($"solve:done:{contentHash}"), "done");

        // 3. ACK le message dans le stream
        await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);

        // 4. Libère le verrou de déduplication
        await db.KeyDeleteAsync($"solve-lock:{contentHash}");

        logger.LogInformation("[{WorkerId}] {ContentHash} done. Found={Found}",
            _workerId, contentHash, response.Found);
    }

    private async Task<SolveResponse> Solve(SolveRequest request, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var boardSet = request.Board.ToSet();
            var rackSet = new Set(request.Hand);

            IStrategy strategy = new GraphStrategy();
            var result = await strategy.GetSolverResult(boardSet, rackSet, request.HasPlayed, ct);

            stopwatch.Stop();

            logger.LogInformation("[{WorkerId}] Solver {Source} completed in {Elapsed}ms. Found={Found}",
                _workerId, result.Source, stopwatch.ElapsedMilliseconds, result.Found);

            if (!result.Found)
            {
                return new SolveResponse
                {
                    RequestId = request.RequestId,
                    Found = false,
                    NewBoard = request.Board,
                    WorkerId = _workerId
                };
            }

            return new SolveResponse
            {
                RequestId = request.RequestId,
                Found = true,
                NewBoard = result.BestSolution,
                TilesToPlay = result.TilesToPlay.ToList(),
                JokersToPlay = result.JokersToPlay,
                Score = result.Score,
                WorkerId = _workerId
            };
        }
        catch (OperationCanceledException)
        {
            return new SolveResponse
            {
                RequestId = request.RequestId,
                Found = false,
                WorkerId = _workerId,
                Error = "Solve cancelled"
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{WorkerId}] Solver error for request {RequestId}", _workerId, request.RequestId);

            return new SolveResponse
            {
                RequestId = request.RequestId,
                Found = false,
                WorkerId = _workerId,
                Error = ex.Message
            };
        }
    }
}
