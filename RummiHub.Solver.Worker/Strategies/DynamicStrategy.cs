using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;

namespace RummiHub.Solver.Worker.Strategies;

/// <summary>
/// Production strategy backed by the polynomial-time <see cref="DynamicSolver"/>.
///
/// <para>
/// The dynamic-programming solver is exact and dramatically faster than the exponential
/// tree search, so its "no move" answer is final: a rack with no legal play, or a first play
/// short of 30 points (which still carries its best score for the UI), is returned as-is.
/// Re-running the exponential search would only reach the same verdict, and once the table
/// gets crowded that takes tens of seconds, long enough to time out the caller.
/// </para>
///
/// <para>
/// As the newest engine, the moves it finds are still wrapped in a safety net: a found move
/// is validated (every set legal, all board tiles preserved, every played tile coming from
/// the rack, first-play score ≥ 30). If validation ever fails, or the engine throws
/// unexpectedly, the strategy transparently falls back to the proven
/// <see cref="GraphStrategy"/>. An <see cref="OperationCanceledException"/> is rethrown,
/// never treated as an engine failure.
/// </para>
/// </summary>
public sealed class DynamicStrategy : IStrategy
{
    private readonly Func<Set, Set, bool, ISolver> _createSolver;
    private readonly IStrategy _fallback;

    public DynamicStrategy() : this(new GraphStrategy())
    {
    }

    /// <summary>Test seam: observe the fallback and, optionally, stand in for the engine.</summary>
    internal DynamicStrategy(IStrategy fallback, Func<Set, Set, bool, ISolver>? createSolver = null)
    {
        _fallback = fallback;
        _createSolver = createSolver ?? CreateSolver;
    }

    public async Task<SolverResult> GetSolverResult(Set board, Set rack, bool hasPlayed,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = _createSolver(board, rack, hasPlayed).SearchSolution(cancellationToken);

            // An exact "no move" is final; only a move the engine found needs double-checking.
            // A first play is laid beside the table, never on it: check it against no board.
            var playedOn = hasPlayed ? board : new Set();
            if (!result.Found || SolutionValidator.IsValid(result, playedOn, rack, hasPlayed))
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

    private static ISolver CreateSolver(Set board, Set rack, bool hasPlayed) =>
        hasPlayed
            ? DynamicSolver.Create(board, rack)
            : DynamicSolver.CreateFirst(rack);
}
