var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedisStack("cache")
    .WithRedisInsight(insight => insight.WithHostPort(41245));

var apiService = builder.AddProject<Projects.RummiHub_ApiService>("apiservice")
    .WithReference(cache)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health")
    .WithUrl("https://api.rummihub.lan");

builder.AddProject<Projects.RummiHub_Solver_Worker>("solver-worker")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReplicas(2);

builder.AddProject<Projects.RummiHub_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(apiService)
    .WaitFor(apiService)
    .WithUrl("https://rummihub.lan");

builder.Build().Run();
