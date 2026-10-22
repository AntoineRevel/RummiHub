using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.AddRedisClient("cache");

var solverOptions = builder.Configuration.GetSection(SolverApiOptions.SectionName).Get<SolverApiOptions>()
                    ?? new SolverApiOptions();
var solverKey = solverOptions.Key?.Trim();
if (string.IsNullOrEmpty(solverKey) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "SolverApi:Key is required outside Development. Set SolverApi__Key to the same secret on apiservice and webfrontend.");
}

builder.Services.Configure<SolverApiOptions>(builder.Configuration.GetSection(SolverApiOptions.SectionName));

var permitLimit = solverOptions.PermitLimit > 0 ? solverOptions.PermitLimit : SolverApiOptions.DefaultPermitLimit;
var windowSeconds = solverOptions.WindowSeconds > 0 ? solverOptions.WindowSeconds : SolverApiOptions.DefaultWindowSeconds;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("solve", limiter =>
    {
        limiter.PermitLimit = permitLimit;
        limiter.Window = TimeSpan.FromSeconds(windowSeconds);
        limiter.QueueLimit = 0;
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    foreach (var converter in RummiJsonOptions.Default.Converters)
        options.SerializerOptions.Converters.Add(converter);

    options.SerializerOptions.PropertyNamingPolicy = RummiJsonOptions.Default.PropertyNamingPolicy;
    options.SerializerOptions.MaxDepth = 32;
});

var app = builder.Build();

var allowAnonymousSolverCalls = string.IsNullOrEmpty(solverKey) && app.Environment.IsDevelopment();
if (allowAnonymousSolverCalls)
{
    app.Logger.LogWarning(
        "SolverApi:Key is empty; /solve accepts anonymous callers because the host is Development. Set SolverApi__Key before any public deployment.");
}

app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/solve"))
    {
        if (context.Request.ContentLength is > SolverApiAuth.MaxRequestBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
            bodySize.MaxRequestBodySize = SolverApiAuth.MaxRequestBodyBytes;
    }

    await next(context);
});

app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

async ValueTask<object?> AuthorizeSolve(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var presented = context.HttpContext.Request.Headers[SolverApiAuth.HeaderName].ToString();
    if (!SolverApiAuth.IsCallerAllowed(solverKey, presented, allowAnonymousSolverCalls))
        return Results.Unauthorized();

    return await next(context);
}

app.MapGet("/", () => "RummiHub API");

app.MapGet("/ping", () => Results.Ok(new { Status = "ok", Timestamp = DateTime.UtcNow }));

// POST /solve : validates bounds, then computes a content hash, deduplicates, and enqueues.
app.MapPost("/solve", async (SolveRequest request, IConnectionMultiplexer redis) =>
{
    if (!SolveRequestValidator.TryValidate(request, out var error))
        return Results.BadRequest(new { error });

    var db = redis.GetDatabase();

    var contentHash = ContentHasher.ComputeHash(request.Hand, request.Board, request.HasPlayed);

    // 1. Check if result is already cached
    var cached = await db.ExecuteAsync("JSON.GET", RedisKeys.SolveResult(contentHash));

    if (cached.IsNull)
    {
        // 2. Try to acquire the deduplication lock (SET NX, TTL 10 min)
        var lockAcquired = await db.StringSetAsync(
            RedisKeys.SolveLock(contentHash),
            request.RequestId,
            TimeSpan.FromMinutes(10),
            When.NotExists);

        if (lockAcquired)
        {
            // 3. Enqueue into the Redis Stream — priority requests (move validation)
            //    go to a dedicated stream the workers drain first.
            var streamKey = request.Priority ? RedisKeys.SolvePriorityStream : RedisKeys.SolveStream;
            var payload = JsonSerializer.Serialize(request, RummiJsonOptions.Default);
            await db.StreamAddAsync(streamKey, [
                new NameValueEntry("contentHash", contentHash),
                new NameValueEntry("payload", payload)
            ], maxLength: 1000, useApproximateMaxLength: true);

            // 4. Wake up waiting workers
            var subscriber = redis.GetSubscriber();
            await subscriber.PublishAsync(RedisChannel.Literal(RedisKeys.SolveEnqueueChannel), "new");
        }
    }

    return Results.Accepted($"/solve/{contentHash}", new
    {
        ContentHash = contentHash,
        request.RequestId,
        Status = cached.IsNull ? "processing" : "completed"
    });
}).RequireRateLimiting("solve").AddEndpointFilter(AuthorizeSolve);

// GET /solve/{contentHash} : reads result from cache. Same auth as POST; the id must be
// the 16-char hex hash the API itself issues.
app.MapGet("/solve/{contentHash}", async (string contentHash, IConnectionMultiplexer redis) =>
{
    if (!SolveRequestValidator.IsContentHash(contentHash))
        return Results.BadRequest(new { error = "Unknown solve id." });

    var db = redis.GetDatabase();
    var json = await db.ExecuteAsync("JSON.GET", RedisKeys.SolveResult(contentHash));

    if (json.IsNull)
        return Results.Ok(new { ContentHash = contentHash, Status = "pending" });

    var response = JsonSerializer.Deserialize<SolveResponse>(json.ToString()!, RummiJsonOptions.Default);
    return Results.Ok(response);
}).RequireRateLimiting("solve").AddEndpointFilter(AuthorizeSolve);

app.MapDefaultEndpoints();

app.Run();
