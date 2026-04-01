using RummiHub.Core.Models;

namespace RummiHub.Core.Solver;

public interface IStrategy
{
    Task<SolverResult> GetSolverResult(Set board, Set rack, bool hasPlayed,
        CancellationToken cancellationToken = default);
}
