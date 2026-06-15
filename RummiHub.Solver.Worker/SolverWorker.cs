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
    private const string StreamKey = RedisKeys.SolveStream;
    private const string PriorityStreamKey = RedisKeys.SolvePriorityStream;
    private const string GroupName = RedisKeys.SolversGroup;
    private static readonly RedisChannel EnqueueChannel = RedisChannel.Literal(RedisKeys.SolveEnqueueChannel);

    // Un message pending dont le consumer ne donne plus signe de vie depuis ce délai
    // est considéré abandonné (worker crashé) et peut être réclamé via XAUTOCLAIM.
    private static readonly TimeSpan AbandonedMessageIdleTime = TimeSpan.FromMinutes(1);

    // Réveil périodique de la boucle même sans notification Pub/Sub, pour vérifier
    // les messages abandonnés (le nom de consumer change à chaque démarrage, donc
    // les pending d'un worker mort ne sont jamais relus par son propre PEL).
    private static readonly TimeSpan IdleWakeInterval = TimeSpan.FromSeconds(30);

    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid().ToString()[..8]}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[{WorkerId}] Solver worker started", _workerId);
        var db = redis.GetDatabase();

        // Crée le consumer group sur les deux streams (normal + prioritaire) s'il n'existe pas encore
        await EnsureConsumerGroup(db, StreamKey);
        await EnsureConsumerGroup(db, PriorityStreamKey);

        // Boucle principale — lit les nouveaux messages et réclame les abandonnés
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
                // Le stream prioritaire (validations de coups) est toujours vidé avant
                // le stream normal (hints optimaux) pour que les requêtes rapides ne
                // fassent pas la queue derrière les gros solves de chevalet complet.
                if (await TryReadAndProcess(db, PriorityStreamKey, stoppingToken)) continue;
                if (await TryReadAndProcess(db, StreamKey, stoppingToken)) continue;

                // Pas de nouveau message : tente de réclamer un message abandonné
                // par un worker mort (prioritaire d'abord), sinon attend un réveil.
                if (await TryClaimAndProcess(db, PriorityStreamKey, stoppingToken)) continue;
                if (await TryClaimAndProcess(db, StreamKey, stoppingToken)) continue;

                await WaitForWakeSignal(wakeSignal.Reader, stoppingToken);
            }
        }
        finally
        {
            await subscriber.UnsubscribeAsync(EnqueueChannel);
            wakeSignal.Writer.TryComplete();
        }
    }

    private async Task EnsureConsumerGroup(IDatabase db, string streamKey)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(streamKey, GroupName, "0-0", createStream: true);
            logger.LogInformation("Consumer group '{GroupName}' created on stream '{StreamKey}'", GroupName, streamKey);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
        {
            // Le groupe existe déjà — c'est normal avec plusieurs replicas
        }
    }

    /// <summary>Lit une entrée du stream donné et la traite. Retourne true si une entrée a été traitée.</summary>
    private async Task<bool> TryReadAndProcess(IDatabase db, string streamKey, CancellationToken ct)
    {
        var entries = await db.StreamReadGroupAsync(streamKey, GroupName, _workerId, count: 1);
        if (entries.Length == 0)
            return false;

        await ProcessEntry(db, streamKey, entries[0], ct);
        return true;
    }

    /// <summary>Réclame puis traite un message abandonné du stream donné. Retourne true si traité.</summary>
    private async Task<bool> TryClaimAndProcess(IDatabase db, string streamKey, CancellationToken ct)
    {
        var entry = await ClaimAbandonedEntry(db, streamKey);
        if (entry is not { } e)
            return false;

        await ProcessEntry(db, streamKey, e, ct);
        return true;
    }

    /// <summary>
    /// Réclame un message pending d'un consumer mort (XAUTOCLAIM). Sans cela, un
    /// message non-ACKé après un crash resterait orphelin pour toujours : le nom de
    /// consumer contient un GUID régénéré à chaque démarrage, donc personne ne relit
    /// jamais le PEL du consumer disparu.
    /// </summary>
    private async Task<StreamEntry?> ClaimAbandonedEntry(IDatabase db, string streamKey)
    {
        var result = await db.StreamAutoClaimAsync(
            streamKey, GroupName, _workerId,
            (long)AbandonedMessageIdleTime.TotalMilliseconds,
            "0-0", count: 1);

        if (result.ClaimedEntries.Length == 0)
            return null;

        var entry = result.ClaimedEntries[0];
        logger.LogWarning("[{WorkerId}] Claimed abandoned message {EntryId} from a dead consumer on '{StreamKey}'",
            _workerId, entry.Id, streamKey);
        return entry;
    }

    private static async Task WaitForWakeSignal(ChannelReader<bool> reader, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(IdleWakeInterval);
        try
        {
            await reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Réveil périodique : on repart vérifier le stream et les abandonnés.
        }
    }

    private async Task ProcessEntry(IDatabase db, string streamKey, StreamEntry entry, CancellationToken ct)
    {
        var contentHash = entry["contentHash"].ToString();
        var payload = entry["payload"];

        // Entrée vide (ex. supprimée par le trim du stream) : ACK et on passe.
        if (payload.IsNullOrEmpty)
        {
            await db.StreamAcknowledgeAsync(streamKey, GroupName, entry.Id);
            return;
        }

        var request = JsonSerializer.Deserialize<SolveRequest>(payload.ToString(), RummiJsonOptions.Default);
        if (request is null)
        {
            await db.StreamAcknowledgeAsync(streamKey, GroupName, entry.Id);
            return;
        }

        logger.LogInformation("[{WorkerId}] Processing {ContentHash} (request {RequestId})",
            _workerId, contentHash, request.RequestId);

        var response = await Solve(request, ct);
        var resultJson = JsonSerializer.Serialize(response, RummiJsonOptions.Default);

        // 1. Stocke le résultat en JSON natif (1h TTL)
        await db.ExecuteAsync("JSON.SET", RedisKeys.SolveResult(contentHash), "$", resultJson);
        await db.KeyExpireAsync(RedisKeys.SolveResult(contentHash), TimeSpan.FromHours(1));

        // 2. Publie la notification pour les subscribers (Blazor Server)
        var subscriber = redis.GetSubscriber();
        await subscriber.PublishAsync(RedisChannel.Literal(RedisKeys.SolveDoneChannel(contentHash)), "done");

        // 3. ACK le message dans le stream
        await db.StreamAcknowledgeAsync(streamKey, GroupName, entry.Id);

        // 4. Libère le verrou de déduplication
        await db.KeyDeleteAsync(RedisKeys.SolveLock(contentHash));

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

            IStrategy strategy = new DynamicStrategy();
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
                    // Carries the best first-play score even when below 30, so the UI can show it.
                    Score = result.Score,
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
