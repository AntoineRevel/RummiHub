using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;

namespace RummiHub.Solver.Worker.Strategies;

public class GraphStrategy : IStrategy
{
    public Task<SolverResult> GetSolverResult(Set board, Set rack, bool hasPlayed,
        CancellationToken cancellationToken = default)
    {
        ISolver graphSolver = hasPlayed
            ? GraphSolver.Create(board, rack)
            : GraphFirstSolver.Create(rack);

        return Task.Run(() => graphSolver.SearchSolution(cancellationToken), cancellationToken);
    }
}
