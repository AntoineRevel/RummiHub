var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");

var apiService = builder.AddProject<Projects.RummiHub_ApiService>("apiservice")
    .WithReference(cache)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health")
    .WithUrl("https://api.rummihub.local");

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
    .WithUrl("https://rummihub.local");

builder.Build().Run();
