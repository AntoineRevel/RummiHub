using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Enregistre le client Redis (le nom "cache" doit correspondre à celui dans AppHost.cs)
builder.AddRedisClient("cache");

// Permet d'envoyer/recevoir les enums en texte ("Red") plutôt qu'en entier (0)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

// Options JSON partagées pour sérialiser/désérialiser manuellement (vers Redis)
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

// POST /solve : on pousse la requête dans la file Redis, le worker va la traiter
app.MapPost("/solve", async (SolveRequest request, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();

    // Sérialise la requête en JSON et la pousse dans la liste Redis "solve-queue"
    // LPUSH = insère à gauche (le worker lira à droite avec RPOP → ordre FIFO)
    var json = JsonSerializer.Serialize(request, jsonOptions);
    await db.ListLeftPushAsync("solve-queue", json);

    // On répond immédiatement avec 202 Accepted : le travail est en cours
    return Results.Accepted($"/solve/{request.RequestId}", new
    {
        request.RequestId,
        Status = "queued",
        ResultUrl = $"/solve/{request.RequestId}"
    });
});

// GET /solve/{requestId} : on lit le résultat stocké par le worker dans Redis
app.MapGet("/solve/{requestId}", async (string requestId, IConnectionMultiplexer redis) =>
{
    var db = redis.GetDatabase();

    // Le worker stocke le résultat sous la clé "solve-result:{requestId}"
    var json = await db.StringGetAsync($"solve-result:{requestId}");

    if (json.IsNull)
        return Results.Ok(new { RequestId = requestId, Status = "pending" });

    var response = JsonSerializer.Deserialize<SolveResponse>((string)json!, jsonOptions);
    return Results.Ok(response);
});

app.MapDefaultEndpoints();

app.Run();
