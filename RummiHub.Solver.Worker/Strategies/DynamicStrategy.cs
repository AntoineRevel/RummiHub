using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;

namespace RummiHub.Solver.Worker.Strategies;

/// <summary>
/// Production strategy backed by the polynomial-time <see cref="DynamicSolver"/>.
///
/// <para>
/// The dynamic-programming solver is exact and dramatically faster than the exponential
/// tree search, but as the newest engine it is wrapped in a safety net: its result is
/// validated (every set legal, all board tiles preserved, every played tile coming from
/// the rack, first-play score ≥ 30). If validation ever fails, the strategy transparently
/// falls back to the proven <see cref="GraphStrategy"/>, so production can never regress
/// below the previous behavior.
/// </para>
/// </summary>
public sealed class DynamicStrategy : IStrategy
{
    private readonly GraphStrategy _fallback = new();

    public async Task<SolverResult> GetSolverResult(Set board, Set rack, bool hasPlayed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ISolver solver = hasPlayed
                ? DynamicSolver.Create(board, rack)
                : DynamicSolver.CreateFirst(rack);

            var result = solver.SearchSolution(cancellationToken);

            if (result.Found && SolutionValidator.IsValid(result, board, rack, hasPlayed))
                return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Any unexpected failure in the new engine must never break a solve.
        }

        return await _fallback.GetSolverResult(board, rack, hasPlayed, cancellationToken);
    }
}
