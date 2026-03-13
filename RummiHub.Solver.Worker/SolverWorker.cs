using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using StackExchange.Redis;

namespace RummiHub.Solver.Worker;

// IConnectionMultiplexer est injecté automatiquement grâce à AddRedisClient() dans Program.cs
public class SolverWorker(IConnectionMultiplexer redis, ILogger<SolverWorker> logger) : BackgroundService
{
    // Identifiant unique généré une fois au démarrage du worker
    // Comme l'AppHost lance 2 replicas, chaque instance aura son propre ID
    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid().ToString()[..8]}";

    // Options JSON pour lire/écrire les enums en texte ("Red" au lieu de 0)
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Solver worker started, waiting for jobs...");
        var db = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            // RPOP = retire le dernier élément de la liste Redis "solve-queue"
            // Combiné avec LPUSH côté API → comportement FIFO (premier arrivé, premier traité)
            var value = await db.ListRightPopAsync("solve-queue");

            if (value.IsNull)
            {
                // File vide → on attend un peu avant de réessayer
                await Task.Delay(500, stoppingToken);
                continue;
            }

            // Désérialise la requête JSON reçue de Redis
            var request = JsonSerializer.Deserialize<SolveRequest>((string)value!, JsonOptions);
            if (request is null) continue;

            logger.LogInformation("[{WorkerId}] Processing request {RequestId}", _workerId, request.RequestId);

            // --- Logique métier : résolution du plateau Rummikub ---
            var response = await Solve(request, _workerId, stoppingToken);
            // --------------------------------------------------------

            // Sérialise le résultat et le stocke dans Redis pendant 1 heure
            // L'API le lira avec GET /solve/{requestId}
            var resultJson = JsonSerializer.Serialize(response, JsonOptions);
            await db.StringSetAsync($"solve-result:{request.RequestId}", resultJson, TimeSpan.FromHours(1));

            logger.LogInformation("[{WorkerId}] Request {RequestId} done. Solved={Solved}", _workerId, request.RequestId, response.Solved);
        }
    }

    private static async Task<SolveResponse> Solve(SolveRequest request, string workerId, CancellationToken ct = default)
    {
        // Simule un temps de calcul aléatoire entre 1 et 6 secondes
        var delay = Random.Shared.Next(1000, 6000);
        await Task.Delay(delay, ct);

        // Pour l'instant on renvoie un résultat "non résolu"
        // → c'est ici qu'on branchera l'algorithme de résolution plus tard
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
