using System.Globalization;
using System.Text;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Report;

/// <summary>Renders a <see cref="ComparisonResult"/> as CSV (detail rows) or Markdown (summary + detail).</summary>
public static class ReportFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string DetailCsv(ComparisonResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("solver,positionId,phase,found,tiles,jokers,score,legal,matchesOptimum," +
                      "timeMedianMs,timeP95Ms,allocBytesPerSolve,gc0,gc1,gc2,cpuMs,cpuRatio,timedOut,note");

        foreach (var r in result.Rows)
        {
            sb.Append(Csv(r.SolverName)).Append(',')
                .Append(Csv(r.PositionId)).Append(',')
                .Append(r.Phase).Append(',')
                .Append(r.Found).Append(',')
                .Append(r.TilesPlayed).Append(',')
                .Append(r.JokersPlayed).Append(',')
                .Append(r.Score).Append(',')
                .Append(r.Legal).Append(',')
                .Append(r.MatchesOptimum).Append(',')
                .Append(Num(r.TimeMsMedian)).Append(',')
                .Append(Num(r.TimeMsP95)).Append(',')
                .Append(r.AllocBytesPerSolve).Append(',')
                .Append(r.Gc0).Append(',')
                .Append(r.Gc1).Append(',')
                .Append(r.Gc2).Append(',')
                .Append(Num(r.CpuMsTotal)).Append(',')
                .Append(Num(r.CpuRatio)).Append(',')
                .Append(r.TimedOut).Append(',')
                .Append(Csv(r.OptimumNote))
                .AppendLine();
        }

        return sb.ToString();
    }

    public static string SummaryMarkdown(ComparisonResult result)
    {
        var sb = new StringBuilder();
        sb.Append("# Solver performance comparison\n\n");
        sb.Append($"- Source: {result.Source}\n");
        sb.Append($"- Positions: {result.PositionCount}\n");
        sb.Append($"- Repetitions: {result.Options.Repetitions} (warmup {result.Options.WarmupRuns}), " +
                  $"timeout {result.Options.Timeout.TotalMilliseconds:0} ms\n");
        sb.Append($"- Illegal: {result.IllegalCount} · Divergences vs DP: {result.DivergenceCount} · " +
                  $"Timeouts: {result.TimeoutCount}\n");
        sb.Append("- Measurements are indicative (sequential, warmed up, median/p95), not BenchmarkDotNet.\n");
        sb.Append("- Best = fastest solver that completed every position legally with no divergence vs DP.\n");

        foreach (var phase in new[] { BenchPhase.FirstPlay, BenchPhase.DuringGame })
        {
            var ranks = BenchRanking.ForPhase(result.Summaries, phase);
            if (ranks.Count == 0) continue;

            var best = ranks.FirstOrDefault(r => r.IsBest);
            sb.Append($"\n## {phase}\n\n");
            sb.Append(best is null
                ? "🏆 Best: none (no solver was complete and correct).\n\n"
                : $"🏆 Best: **{best.SolverName}** — {Num(best.Summary.TimeMsMedian)} ms median, " +
                  $"{Bytes(best.Summary.AllocBytesMean)}/solve.\n\n");

            sb.Append("| # | Solver | Median ms | p95 ms | Alloc/solve | GC 0/1/2 | CPU/wall | vs DP | Verdict |\n");
            sb.Append("|--:|---|--:|--:|--:|--:|--:|--:|---|\n");
            foreach (var r in ranks)
            {
                var s = r.Summary;
                sb.Append($"| {(r.Qualified ? Rank(r.Rank) : "—")} | {Mark(s.IsBaseline)}{(r.IsBest ? "**" : "")}{s.SolverName}{(r.IsBest ? "**" : "")} | " +
                          $"{Num(s.TimeMsMedian)} | {Num(s.TimeMsP95)} | {Bytes(s.AllocBytesMean)} | " +
                          $"{s.Gc0}/{s.Gc1}/{s.Gc2} | {Num(s.CpuRatioMean)} | {(r.Qualified ? Speedup(r.SpeedupVsBaseline) : "—")} | {r.Verdict} |\n");
            }
        }

        return sb.ToString();
    }

    private static string Rank(int rank) => rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => rank.ToString() };

    private static string Speedup(double s) => s <= 0 ? "—" : $"{s:0.0}×";

    public static string FullMarkdown(ComparisonResult result)
    {
        var sb = new StringBuilder(SummaryMarkdown(result));
        sb.Append("\n## Detail\n\n");
        sb.Append("| Solver | Position | Found | Tiles | Score | Legal | =opt? | Median ms | p95 ms | Alloc | CPU/wall | Note |\n");
        sb.Append("|---|---|--:|--:|--:|:-:|:-:|--:|--:|--:|--:|---|\n");
        foreach (var r in result.Rows)
        {
            var legal = r.TimedOut ? "—" : !r.Found ? "—" : r.Legal ? "✓" : "✗";
            var opt = r.TimedOut ? "TIMEOUT" : r.MatchesOptimum ? "✓" : "✗";
            sb.Append($"| {Mark(r.IsBaseline)}{r.SolverName} | {r.PositionId} | {r.Found} | {r.RackTilesPlayed} | " +
                      $"{r.Score} | {legal} | {opt} | {Num(r.TimeMsMedian)} | {Num(r.TimeMsP95)} | " +
                      $"{Bytes(r.AllocBytesPerSolve)} | {Num(r.CpuRatio)} | {r.OptimumNote} |\n");
        }

        return sb.ToString();
    }

    private static string Mark(bool baseline) => baseline ? "★ " : "";

    private static string Num(double v) => v.ToString("0.###", Inv);

    private static string Bytes(double v)
    {
        if (v >= 1024 * 1024) return (v / (1024 * 1024)).ToString("0.##", Inv) + " MB";
        if (v >= 1024) return (v / 1024).ToString("0.##", Inv) + " KB";
        return v.ToString("0", Inv) + " B";
    }

    private static string Csv(string? value)
    {
        value ??= "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return '"' + value.Replace("\"", "\"\"") + '"';
        return value;
    }
}
