using RummiHub.Solver.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Enregistre le client Redis (même nom "cache" que dans AppHost.cs)
builder.AddRedisClient("cache");

builder.Services.AddHostedService<SolverWorker>();

var host = builder.Build();
host.Run();
