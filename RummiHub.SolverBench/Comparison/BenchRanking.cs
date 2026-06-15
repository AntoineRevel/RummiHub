using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Comparison;

/// <summary>A solver's standing within its phase, with the winner made explicit.</summary>
public sealed record SolverRank
{
    public required SolverSummary Summary { get; init; }

    /// <summary>1-based position within the phase (qualified solvers first, then by speed).</summary>
    public int Rank { get; init; }

    /// <summary>Complete and correct over the corpus: no timeout, no illegal move, no divergence.</summary>
    public bool Qualified { get; init; }

    /// <summary>The fastest qualified solver of the phase — the recommended one.</summary>
    public bool IsBest { get; init; }

    /// <summary>Share of the fastest qualified solver's speed in 0..1 (1 = fastest). 0 if unqualified.</summary>
    public double SpeedScore { get; init; }

    /// <summary>Baseline median ÷ this median (&gt;1 = faster than the DynamicSolver baseline).</summary>
    public double SpeedupVsBaseline { get; init; }

    /// <summary>"clean", or the disqualifying issues (e.g. "4 timeouts, 1 divergence").</summary>
    public string Verdict { get; init; } = "";

    public string SolverName => Summary.SolverName;
}

/// <summary>
/// Turns per-solver summaries into a clear ranking that surfaces the best solver per phase.
///
/// <para>Ranking by raw median time alone is misleading: a solver that times out on the hard
/// positions looks "fast" because only its easy, completed solves count toward the median. So a
/// solver must first be <b>qualified</b> — complete and correct over the corpus (no timeout, no
/// illegal move, no divergence from the baseline) — and only then is it ranked by speed (median
/// time, then allocations). The best solver is the fastest qualified one.</para>
/// </summary>
public static class BenchRanking
{
    public static IReadOnlyList<SolverRank> ForPhase(IEnumerable<SolverSummary> summaries, BenchPhase phase)
    {
        var inPhase = summaries.Where(s => s.Phase == phase).ToList();
        if (inPhase.Count == 0) return [];

        var baselineMedian = inPhase.FirstOrDefault(s => s.IsBaseline)?.TimeMsMedian ?? 0;
        var fastestQualified = inPhase.Where(IsQualified).Select(s => s.TimeMsMedian)
            .DefaultIfEmpty(0).Min();

        var ordered = inPhase
            .OrderBy(s => IsQualified(s) ? 0 : 1)               // qualified first
            .ThenBy(s => IsQualified(s) ? 0 : IssueCount(s))    // then unqualified by fewest issues
            .ThenBy(s => s.TimeMsMedian)                        // then by speed
            .ThenBy(s => s.AllocBytesMean)                      // then by memory
            .ToList();

        var bestName = ordered.FirstOrDefault(IsQualified)?.SolverName;

        return ordered.Select((s, i) => new SolverRank
        {
            Summary = s,
            Rank = i + 1,
            Qualified = IsQualified(s),
            IsBest = IsQualified(s) && s.SolverName == bestName,
            SpeedScore = IsQualified(s) && s.TimeMsMedian > 0 ? fastestQualified / s.TimeMsMedian : 0,
            SpeedupVsBaseline = s.TimeMsMedian > 0 ? baselineMedian / s.TimeMsMedian : 0,
            Verdict = Verdict(s)
        }).ToList();
    }

    /// <summary>The recommended solver for a phase, or null if none is complete and correct.</summary>
    public static SolverRank? Best(IEnumerable<SolverSummary> summaries, BenchPhase phase) =>
        ForPhase(summaries, phase).FirstOrDefault(r => r.IsBest);

    private static bool IsQualified(SolverSummary s) =>
        s.Timeouts == 0 && s.Illegal == 0 && s.Divergences == 0;

    private static int IssueCount(SolverSummary s) => s.Illegal + s.Divergences + s.Timeouts;

    private static string Verdict(SolverSummary s)
    {
        if (IsQualified(s)) return "clean";
        var parts = new List<string>();
        if (s.Illegal > 0) parts.Add($"{s.Illegal} illegal");
        if (s.Divergences > 0) parts.Add($"{s.Divergences} divergence{(s.Divergences > 1 ? "s" : "")}");
        if (s.Timeouts > 0) parts.Add($"{s.Timeouts} timeout{(s.Timeouts > 1 ? "s" : "")}");
        return string.Join(", ", parts);
    }
}
