using System.Text.Json;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.AddRedisClient("cache");

builder.Services.ConfigureHttpJsonOptions(options =>
{
    foreach (var converter in RummiJsonOptions.Default.Converters)
        options.SerializerOptions.Converters.Add(converter);

    options.SerializerOptions.PropertyNamingPolicy = RummiJsonOptions.Default.PropertyNamingPolicy;
});

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => "RummiHub API");

app.MapGet("/ping", () => Results.Ok(new { Status = "ok", Timestamp = DateTime.UtcNow }));

// POST /solve : computes content hash, deduplicates, and enqueues into Redis stream
app.MapPost("/solve", async (SolveRequest request, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();

    var contentHash = ContentHasher.ComputeHash(request.Hand, request.Board, request.HasPlayed);

    // 1. Check if result is already cached
    var cached = await db.StringGetAsync($"solve-result:{contentHash}");

    if (cached.IsNull)
    {
        // 2. Try to acquire the deduplication lock (SET NX, TTL 10 min)
        var lockAcquired = await db.StringSetAsync(
            $"solve-lock:{contentHash}",
            request.RequestId,
            TimeSpan.FromMinutes(10),
            When.NotExists);

        if (lockAcquired)
        {
            // 3. Enqueue into Redis Stream
            var payload = JsonSerializer.Serialize(request, RummiJsonOptions.Default);
            await db.StreamAddAsync("solve-stream", [
                new NameValueEntry("contentHash", contentHash),
                new NameValueEntry("payload", payload)
            ], maxLength: 1000, useApproximateMaxLength: true);

            // 4. Wake up waiting workers
            var subscriber = redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(RedisChannels.SolveEnqueue), "new");
        }
    }

    return Results.Accepted($"/solve/{contentHash}", new
    {
        ContentHash = contentHash,
        request.RequestId,
        Status = cached.IsNull ? "processing" : "completed"
    });
});

// GET /solve/{contentHash} : reads result from cache
app.MapGet("/solve/{contentHash}", async (string contentHash, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();
    var json = await db.StringGetAsync($"solve-result:{contentHash}");

    if (json.IsNull)
        return Results.Ok(new { ContentHash = contentHash, Status = "pending" });

    var response = JsonSerializer.Deserialize<SolveResponse>((string)json!, RummiJsonOptions.Default);
    return Results.Ok(response);
});

app.MapDefaultEndpoints();

app.Run();
