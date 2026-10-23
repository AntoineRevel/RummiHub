using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

// Shared secret for /solve. Generated and persisted for local runs. Publish does not
// embed the value: set the solver-api-key parameter (SolverApi__Key on both projects)
// at deploy time. Special characters are omitted so the value is a legal HTTP header.
var solverApiKey = builder.AddParameter(
    "solver-api-key",
    new GenerateParameterDefault
    {
        MinLength = 32,
        Lower = true,
        Upper = true,
        Numeric = true,
        Special = false
    },
    secret: true,
    persist: true);

// Official Redis 8 (library/redis:8.10.2) bundles the former Stack modules.
// Aspire starts the container with entrypoint /bin/sh, so the image entrypoint
// that auto-loads every module under /usr/local/lib/redis/modules does not run.
// The solve pipeline only needs RedisJSON (JSON.GET / JSON.SET).
var cache = builder.AddRedis("cache")
    .WithImageRegistry("docker.io")
    .WithImage("library/redis", "8.10.2")
    .WithModule(RedisModules.Json)
    .WithRedisInsight(insight => insight.WithHostPort(41245));

// The solver API is an internal service. Do not call WithExternalHttpEndpoints on it:
// only the web frontend is reachable from the public internet.
var apiService = builder.AddProject<Projects.RummiHub_ApiService>("apiservice")
    .WithReference(cache)
    .WaitFor(cache)
    .WithHttpHealthCheck("/health")
    .WithEnvironment("SolverApi__Key", solverApiKey)
    .WithEndpoint("http", endpoint => endpoint.IsExternal = false, createIfNotExists: false)
    .WithEndpoint("https", endpoint => endpoint.IsExternal = false, createIfNotExists: false);

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
    .WithEnvironment("SolverApi__Key", solverApiKey);

builder.Build().Run();
