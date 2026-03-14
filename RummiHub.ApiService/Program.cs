using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.AddRedisClient("cache");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

var jsonOptions = new JsonSerializerOptions
{
    Converters = { new JsonStringEnumConverter() }
};

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => "RummiHub API");

app.MapGet("/ping", () => Results.Ok(new { Status = "ok", Timestamp = DateTime.UtcNow }));

// POST /solve : calcule le content hash, déduplique, et enqueue dans le stream Redis
app.MapPost("/solve", async (SolveRequest request, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();

    // Hash déterministe du contenu (main + plateau) pour déduplication
    var contentHash = ContentHasher.ComputeHash(request.Hand, request.Board);

    // 1. Vérifie si le résultat est déjà en cache
    var cached = await db.StringGetAsync($"solve-result:{contentHash}");

    if (cached.IsNull)
    {
        // 2. Tente d'acquérir le verrou de déduplication (SET NX, TTL 10 min)
        //    Si le verrou existe déjà → une autre requête identique est en cours, on ne relance pas de worker
        var lockAcquired = await db.StringSetAsync(
            $"solve-lock:{contentHash}",
            request.RequestId,
            TimeSpan.FromMinutes(10),
            When.NotExists);

        if (lockAcquired)
        {
            // 3. Enqueue dans le Redis Stream (consumer group côté worker)
            var payload = JsonSerializer.Serialize(request, jsonOptions);
            await db.StreamAddAsync("solve-stream", [
                new NameValueEntry("contentHash", contentHash),
                new NameValueEntry("payload", payload)
            ], maxLength: 1000, useApproximateMaxLength: true);

            // 4. Réveille les workers en attente pour déclencher le traitement immédiatement
            var subscriber = redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal("solve:enqueue"), "new");
        }
    }

    // Retourne toujours le même format avec ContentHash.
    // Le SolveResultSubscriber côté Blazor trouvera le résultat en cache immédiatement
    // grâce au pattern subscribe-then-check (pas besoin d'un format de réponse différent).
    return Results.Accepted($"/solve/{contentHash}", new
    {
        ContentHash = contentHash,
        request.RequestId,
        Status = cached.IsNull ? "processing" : "completed"
    });
});

// GET /solve/{contentHash} : lit le résultat en cache (endpoint fallback)
app.MapGet("/solve/{contentHash}", async (string contentHash, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var json = await db.StringGetAsync($"solve-result:{contentHash}");

    if (json.IsNull)
        return Results.Ok(new { ContentHash = contentHash, Status = "pending" });

    var response = JsonSerializer.Deserialize<SolveResponse>((string)json!, jsonOptions);
    return Results.Ok(response);
});

app.MapDefaultEndpoints();

app.Run();
