using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Comparison;

/// <summary>
/// Heuristics that flag "interesting" positions worth keeping in the bank: technically
/// rich moves (big board rearrangements, many tiles, repositioned jokers) and positions
/// that stress the solvers (slow solve, large performance gap). Returns descriptive tags.
/// </summary>
public static class PositionInterest
{
    public const int HighTileCount = 5;
    public const int BigRearrangementSetDelta = 2;
    public const double SlowSolveMs = 50.0;
    public const double PerfGapFactor = 20.0;

    /// <summary>
    /// Structural interest of a chosen move, known at trace time (before-board + the move).
    /// </summary>
    public static IReadOnlyList<string> Structural(Solution before, SolverResult move)
    {
        var tags = new List<string>();
        if (!move.Found) return tags;

        var jokersPlayed = move.JokersToPlay + move.TilesToPlay.Count(t => t.IsJoker);
        var rackTilesPlayed = move.TilesToPlay.Count(t => !t.IsJoker) + jokersPlayed;

        if (rackTilesPlayed >= HighTileCount)
            tags.Add("high-tile-count");

        if (jokersPlayed >= 1)
            tags.Add("joker-play");

        var beforeSets = before.Runs.Count + before.Groups.Count;
        var afterSets = move.BestSolution.Runs.Count + move.BestSolution.Groups.Count;
        if (beforeSets > 0 && Math.Abs(afterSets - beforeSets) >= BigRearrangementSetDelta)
            tags.Add("big-rearrangement");

        // A board joker that the move re-homes into a different set is a classic tricky case.
        var boardJokers = before.Runs.Concat(before.Groups).SelectMany(s => s.Tiles).Count(t => t.IsJoker);
        if (boardJokers >= 1 && tags.Contains("big-rearrangement"))
            tags.Add("joker-rearrangement");

        return tags;
    }

    /// <summary>
    /// Performance interest, known after a comparison: a slow solve or a wide spread
    /// between the fastest and slowest solver on the same position.
    /// </summary>
    public static IReadOnlyList<string> Performance(IReadOnlyList<SolveMeasurement> rowsForPosition)
    {
        var tags = new List<string>();
        var times = rowsForPosition
            .Where(r => !r.TimedOut)
            .Select(r => r.TimeMsMedian)
            .Where(t => t > 0)
            .ToList();

        if (rowsForPosition.Any(r => r.TimedOut))
            tags.Add("timeout");

        if (times.Count == 0) return tags;

        if (times.Max() >= SlowSolveMs)
            tags.Add("slow-solve");

        if (times.Min() > 0 && times.Max() / times.Min() >= PerfGapFactor)
            tags.Add("perf-gap");

        return tags;
    }
}
