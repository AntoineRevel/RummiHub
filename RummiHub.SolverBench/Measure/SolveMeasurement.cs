using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Measure;

/// <summary>
/// One measured (position × solver) result: the functional outcome (found / tiles / score
/// / legality) plus the performance sample (time, allocations, GC, CPU). The optimum verdict
/// (<see cref="MatchesOptimum"/>) is filled in afterwards by the comparer against the baseline.
///
/// <para>Measurements are indicative, not BenchmarkDotNet-grade: they are taken sequentially
/// with a warmup and a clean GC, reporting median/p95 over a handful of repetitions.</para>
/// </summary>
public sealed record SolveMeasurement
{
    public required string SolverName { get; init; }
    public required string PositionId { get; init; }
    public required BenchPhase Phase { get; init; }
    public bool IsBaseline { get; init; }

    // ── functional outcome ──────────────────────────────────────────────────────────
    public bool TimedOut { get; init; }
    public bool Found { get; init; }

    /// <summary>Real (non-joker) rack tiles played.</summary>
    public int TilesPlayed { get; init; }

    /// <summary>Rack jokers played.</summary>
    public int JokersPlayed { get; init; }

    /// <summary>True legal score of the move (joker-aware), independent of what the solver reported.</summary>
    public int Score { get; init; }

    /// <summary>True when the solver reported a higher score than the move can legally yield (a bug).</summary>
    public bool OverScored { get; init; }

    /// <summary>
    /// Legality of a *claimed* move, verified independently via <c>SolutionValidator</c>.
    /// A "not found" result claims no move and is therefore reported as legal (use
    /// <see cref="Found"/> to tell the two apart).
    /// </summary>
    public bool Legal { get; init; }

    public int RackTilesPlayed => TilesPlayed + JokersPlayed;

    // ── performance sample ──────────────────────────────────────────────────────────
    public int Repetitions { get; init; }
    public double TimeMsMedian { get; init; }
    public double TimeMsP95 { get; init; }
    public double TimeMsMin { get; init; }
    public double TimeMsMean { get; init; }

    /// <summary>Mean managed bytes allocated per solve (process-wide delta ÷ reps).</summary>
    public long AllocBytesPerSolve { get; init; }

    public int Gc0 { get; init; }
    public int Gc1 { get; init; }
    public int Gc2 { get; init; }

    /// <summary>Total processor time over the measured block (captures parallel threads).</summary>
    public double CpuMsTotal { get; init; }
    public double CpuMsUser { get; init; }

    /// <summary>Wall-clock time over the measured block.</summary>
    public double WallMsTotal { get; init; }

    /// <summary>CPU ÷ wall — a parallelism factor (≈1 sequential, &gt;1 multi-threaded).</summary>
    public double CpuRatio => WallMsTotal > 0 ? CpuMsTotal / WallMsTotal : 0;

    // ── verdict (set by the comparer) ───────────────────────────────────────────────
    public bool MatchesOptimum { get; init; }
    public string OptimumNote { get; init; } = "";
}
