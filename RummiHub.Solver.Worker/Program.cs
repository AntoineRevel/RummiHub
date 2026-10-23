using OpenTelemetry.Trace;
using RummiHub.Solver.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Traces : une span par résolution, rattachée à la requête de l'API (SolverWorker), sans
// l'attente de messages (RedisPollingFilter).
builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing
    .AddSource(SolverWorker.ActivitySource.Name)
    .AddProcessor(new RedisPollingFilter()));

// Enregistre le client Redis (même nom "cache" que dans AppHost.cs)
builder.AddRedisClient("cache");

builder.Services.AddHostedService<SolverWorker>();

var host = builder.Build();
host.Run();
