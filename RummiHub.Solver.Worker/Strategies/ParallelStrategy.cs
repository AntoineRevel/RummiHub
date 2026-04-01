using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Combinations;
using RummiHub.Solver.Worker.Solver.Combinations.First;
using RummiHub.Solver.Worker.Solver.Incremental;

namespace RummiHub.Solver.Worker.Strategies;

public class ParallelStrategy : IStrategy
{
    public async Task<SolverResult> GetSolverResult(Set board, Set rack, bool hasPlayed,
        CancellationToken externalToken = default)
    {
        ISolver combiSolver = hasPlayed
            ? ParallelCombinationsSolver.Create(new Set(board), rack)
            : CombinationsFirstSolver.Create(rack);

        ISolver incrementalSolver = hasPlayed
            ? IncrementalComplexSolver.Create(new Set(board), rack)
            : IncrementalFirstBaseSolver.Create(rack);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var token = cts.Token;

        var incrementalTask = Task.Run(() => incrementalSolver.SearchSolution(token), token);
        var combiTask = Task.Run(() => combiSolver.SearchSolution(token), token);

        var completedTask = await Task.WhenAny(incrementalTask, combiTask);

        await cts.CancelAsync();

        return await completedTask;
    }
}
