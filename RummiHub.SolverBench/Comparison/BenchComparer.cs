using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Solvers;

namespace RummiHub.SolverBench.Comparison;

/// <summary>
/// Runs every selected solver over every position and produces a <see cref="ComparisonResult"/>.
///
/// <para>Execution is strictly sequential (zero contention between solvers) so the per-solve
/// measurements are comparable. For each position a fresh <c>DynamicSolver</c> baseline is
/// computed independently of the selection, then every selected solver is compared to it:
/// <list type="bullet">
/// <item><b>During game</b> the shared objective is the number of rack tiles played, so the
///   optimum match requires the same <c>found</c> and the same rack-tiles-played as the baseline.</item>
/// <item><b>First play</b> the solvers satisfice (they look for any legal ≥30 opening rather
///   than the maximum score), so the optimum match only requires agreement on <c>found</c>.</item>
/// </list>
/// Legality is checked independently of this verdict (see <c>SolveMeasurer</c>), so an illegal
/// move and a divergent move are reported separately. The baseline is an optimality oracle,
/// not absolute truth — any divergence is surfaced for investigation.</para>
/// </summary>
public sealed class BenchComparer(MeasureOptions? options = null)
{
    private readonly MeasureOptions _options = options ?? new MeasureOptions();

    public ComparisonResult Compare(
        IReadOnlyList<BenchPosition> positions,
        IReadOnlyList<SolverDescriptor> solvers,
        string source = "")
    {
        var measurer = new SolveMeasurer(_options);
        var rows = new List<SolveMeasurement>();

        foreach (var position in positions)
        {
            var baseline = measurer.Baseline(position);
            foreach (var solver in solvers.Where(s => s.Phase == position.Phase))
            {
                var measurement = measurer.Measure(position, solver);
                var (matches, note) = Verdict(position.Phase, measurement, baseline);
                rows.Add(measurement with { MatchesOptimum = matches, OptimumNote = note });
            }
        }

        return new ComparisonResult
        {
            Source = source,
            PositionCount = positions.Count,
            Rows = rows,
            Summaries = BenchAggregator.Summarize(rows),
            Options = _options
        };
    }

    /// <summary>Compares one measurement to the baseline optimum; returns (matches, note).</summary>
    public static (bool Matches, string Note) Verdict(BenchPhase phase, SolveMeasurement m, ReferenceBest baseline)
    {
        if (m.TimedOut) return (false, "timeout");

        if (m.Found != baseline.Found)
            return (false, m.Found ? "found a move; DP found none" : "found none; DP found a move");

        if (!m.Found) return (true, "");

        if (phase == BenchPhase.DuringGame && m.RackTilesPlayed != baseline.RackTilesPlayed)
            return (false, $"played {m.RackTilesPlayed} rack tiles vs DP {baseline.RackTilesPlayed}");

        return (true, "");
    }
}
