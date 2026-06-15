using RummiHub.SolverBench.Measure;

namespace RummiHub.SolverBench.Comparison;

/// <summary>The full output of a comparison: every measured row plus per-solver summaries.</summary>
public sealed record ComparisonResult
{
    public required string Source { get; init; }
    public required int PositionCount { get; init; }
    public required IReadOnlyList<SolveMeasurement> Rows { get; init; }
    public required IReadOnlyList<SolverSummary> Summaries { get; init; }
    public required MeasureOptions Options { get; init; }

    public int IllegalCount => Rows.Count(r => !r.TimedOut && !r.Legal);
    public int DivergenceCount => Rows.Count(r => !r.TimedOut && !r.MatchesOptimum);
    public int TimeoutCount => Rows.Count(r => r.TimedOut);
}
