using RummiHub.Solver.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddHostedService<SolverWorker>();

var host = builder.Build();
host.Run();
