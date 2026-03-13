namespace RummiHub.Solver.Worker;

public class SolverWorker(ILogger<SolverWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Solver worker started, waiting for jobs...");

        while (!stoppingToken.IsCancellationRequested)
        {
            // TODO: consume SolveRequest from Redis Stream, run solver, publish SolveResponse
            await Task.Delay(1000, stoppingToken);
        }
    }
}
