using RummiHub.SolverBench.Bank;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Solvers;
using RummiHub.SolverBench.Trace;

namespace RummiHub.SolverBench;

/// <summary>
/// One entry point shared by the Blazor page and the headless runner, so both drive the
/// exact same engine and therefore reach the exact same verdicts (the timings differ, the
/// found/legal/optimum verdicts do not — they are deterministic given the corpus + solvers).
/// </summary>
public sealed class BenchFacade(MoveBank? bank = null)
{
    public MoveBank Bank { get; } = bank ?? MoveBank.Default();

    /// <summary>Positions from a reproducible seeded trace.</summary>
    public static IReadOnlyList<BenchPosition> TraceCorpus(int seed, GameTraceOptions options) =>
        GameTraceGenerator.Generate(seed, options).Positions;

    /// <summary>Positions loaded from the file-backed bank.</summary>
    public IReadOnlyList<BenchPosition> BankCorpus() => Bank.Load();

    /// <summary>Runs the comparison sequentially and returns the full result.</summary>
    public static ComparisonResult Run(
        IReadOnlyList<BenchPosition> positions,
        IReadOnlyList<SolverDescriptor> solvers,
        MeasureOptions options,
        string source) =>
        new BenchComparer(options).Compare(positions, solvers, source);

    /// <summary>
    /// Persists positions whose structural interest tags (high tile count, big rearrangement,
    /// repositioned jokers…) are present. Deterministic — no timing involved — so it is suitable
    /// for seeding a versioned corpus. Returns the number of new entries written.
    /// </summary>
    public int BankStructural(IReadOnlyList<BenchPosition> positions, int max = int.MaxValue)
    {
        var added = 0;
        foreach (var position in positions)
        {
            if (added >= max) break;

            var structural = position.Tags.Where(t => t is not ("first-play" or "during-game")).ToList();
            if (structural.Count == 0) continue;

            var entry = position with
            {
                Provenance = string.IsNullOrEmpty(position.Provenance)
                    ? "structural"
                    : position.Provenance + ";structural",
                Notes = "interesting: " + string.Join(", ", structural)
            };

            if (Bank.TryAdd(entry)) added++;
        }

        return added;
    }

    /// <summary>
    /// Persists positions flagged as interesting — structurally (trace tags) or by performance
    /// (slow solve / wide perf gap / timeout) — with provenance and the baseline reference.
    /// Returns the number of new entries written.
    /// </summary>
    public int AutoBankInteresting(IReadOnlyList<BenchPosition> positions, ComparisonResult result)
    {
        var rowsById = result.Rows
            .GroupBy(r => r.PositionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SolveMeasurement>)g.ToList());

        var added = 0;
        foreach (var position in positions)
        {
            var structural = position.Tags.Where(t => t is not ("first-play" or "during-game")).ToList();
            var perf = rowsById.TryGetValue(position.Id, out var rows)
                ? PositionInterest.Performance(rows)
                : [];

            if (structural.Count == 0 && perf.Count == 0) continue;

            var enriched = position with
            {
                Tags = position.Tags.Concat(perf).Distinct().ToList(),
                Provenance = string.IsNullOrEmpty(position.Provenance)
                    ? "auto"
                    : position.Provenance + ";auto",
                Notes = string.IsNullOrEmpty(position.Notes)
                    ? "auto-detected: " + string.Join(", ", structural.Concat(perf).Distinct())
                    : position.Notes
            };

            if (Bank.TryAdd(enriched)) added++;
        }

        return added;
    }
}
