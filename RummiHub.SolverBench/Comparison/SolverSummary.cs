using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Comparison;

/// <summary>Per-solver aggregate over the whole corpus.</summary>
public sealed record SolverSummary
{
    public required string SolverName { get; init; }
    public required BenchPhase Phase { get; init; }
    public bool IsBaseline { get; init; }

    public int Positions { get; init; }
    public int Found { get; init; }

    /// <summary>Median of the per-position median times (ms) — the headline ranking metric.</summary>
    public double TimeMsMedian { get; init; }

    /// <summary>p95 of the per-position median times (ms).</summary>
    public double TimeMsP95 { get; init; }

    public long AllocBytesMean { get; init; }
    public long Gc0 { get; init; }
    public long Gc1 { get; init; }
    public long Gc2 { get; init; }
    public double CpuRatioMean { get; init; }

    public int Illegal { get; init; }
    public int Divergences { get; init; }
    public int Timeouts { get; init; }
}

/// <summary>Rolls per-(position×solver) measurements up into per-solver summaries.</summary>
public static class BenchAggregator
{
    /// <summary>Summaries ranked fastest-first by median time (timeouts/empty sink to the bottom).</summary>
    public static IReadOnlyList<SolverSummary> Summarize(IEnumerable<SolveMeasurement> measurements)
    {
        return measurements
            .GroupBy(m => (m.SolverName, m.Phase))
            .Select(BuildSummary)
            .OrderByDescending(s => s.Positions > 0 && s.Timeouts < s.Positions)
            .ThenBy(s => s.TimeMsMedian)
            .ToList();
    }

    private static SolverSummary BuildSummary(IGrouping<(string Name, BenchPhase Phase), SolveMeasurement> group)
    {
        var rows = group.ToList();
        var completed = rows.Where(r => !r.TimedOut).ToList();
        var medians = completed.Select(r => r.TimeMsMedian).ToList();

        return new SolverSummary
        {
            SolverName = group.Key.Name,
            Phase = group.Key.Phase,
            IsBaseline = rows[0].IsBaseline,
            Positions = rows.Count,
            Found = rows.Count(r => r.Found),
            TimeMsMedian = Statistics.Median(medians),
            TimeMsP95 = Statistics.Percentile(medians, 95),
            AllocBytesMean = completed.Count > 0 ? (long)completed.Average(r => r.AllocBytesPerSolve) : 0,
            Gc0 = completed.Sum(r => (long)r.Gc0),
            Gc1 = completed.Sum(r => (long)r.Gc1),
            Gc2 = completed.Sum(r => (long)r.Gc2),
            CpuRatioMean = completed.Count > 0 ? completed.Average(r => r.CpuRatio) : 0,
            Illegal = rows.Count(r => !r.TimedOut && !r.Legal),
            Divergences = rows.Count(r => !r.TimedOut && !r.MatchesOptimum),
            Timeouts = rows.Count(r => r.TimedOut)
        };
    }
}
