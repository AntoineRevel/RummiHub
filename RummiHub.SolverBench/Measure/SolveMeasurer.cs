using System.Diagnostics;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Solvers;
using RummiHub.Solver.Worker.Solver.Dynamic;

namespace RummiHub.SolverBench.Measure;

/// <summary>Knobs for a measurement run. Defaults are deliberately modest and indicative.</summary>
public sealed record MeasureOptions
{
    public int WarmupRuns { get; init; } = 1;
    public int Repetitions { get; init; } = 5;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>
/// Measures a single solver on a single position, honestly:
/// <list type="bullet">
/// <item>a clean GC (<c>Collect</c> + <c>WaitForPendingFinalizers</c> + <c>Collect</c>) before measuring;</item>
/// <item>a warmup run excluded from timing (JIT, static tables);</item>
/// <item>N repetitions, reporting median + p95 (not just the mean);</item>
/// <item>allocations via <c>GC.GetTotalAllocatedBytes(precise)</c> (process-wide ⇒ counts parallel threads);</item>
/// <item>GC collection counts per generation;</item>
/// <item>CPU via <c>Process.TotalProcessorTime</c>, plus the CPU/wall ratio;</item>
/// <item>a per-solve timeout via <c>CancellationTokenSource</c> — recorded as TIMEOUT, never blocking.</item>
/// </list>
/// Legality is checked independently with <c>SolutionValidator</c>; no solver is trusted as truth.
/// </summary>
public sealed class SolveMeasurer(MeasureOptions? options = null)
{
    private readonly MeasureOptions _options = options ?? new MeasureOptions();

    public SolveMeasurement Measure(BenchPosition position, SolverDescriptor solver)
    {
        // Validation always uses the phase-correct board: empty for an opening play.
        var validationBoard = position.Phase == BenchPhase.FirstPlay ? new Set() : position.BoardSet();
        var rackForValidation = position.HandSet();

        CleanGc();

        // ── correctness + first warmup ────────────────────────────────────────────────
        var (result, timedOut) = RunOnce(position, solver);
        if (timedOut)
            return TimedOut(position, solver);

        // Normalize joker values + score so the independent validator judges the tiles, not the
        // solver's representation choices (some report Score 0 / leave jokers unvalued).
        var normalized = MoveEvaluator.Normalize(result);

        // Legality only applies to a *claimed* move. "Not found" makes no claim, so it is not
        // illegal — it is checked against the baseline as a divergence instead.
        var legal = !result.Found ||
                    SolutionValidator.IsValid(normalized, validationBoard, rackForValidation, position.HasPlayed);
        var overScored = MoveEvaluator.OverScores(result);
        var tilesPlayed = result.TilesToPlay.Count(t => !t.IsJoker);
        var jokersPlayed = result.JokersToPlay + result.TilesToPlay.Count(t => t.IsJoker);

        // extra warmups (kept out of measurement)
        for (var w = 1; w < _options.WarmupRuns; w++)
            RunOnce(position, solver);

        // ── measured repetitions ──────────────────────────────────────────────────────
        var reps = Math.Max(1, _options.Repetitions);
        var times = new List<double>(reps);

        CleanGc();
        // Environment.CpuUsage is process-wide, so it captures threads spawned by parallel solvers.
        var cpu0 = Environment.CpuUsage;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var wall = Stopwatch.StartNew();

        for (var i = 0; i < reps; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var (_, repTimedOut) = RunOnce(position, solver);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (repTimedOut)
            {
                timedOut = true;
                break;
            }
        }

        wall.Stop();
        var allocTotal = GC.GetTotalAllocatedBytes(precise: true) - alloc0;
        var cpu1 = Environment.CpuUsage;
        var cpuTotal = (cpu1.TotalTime - cpu0.TotalTime).TotalMilliseconds;
        var userTotal = (cpu1.UserTime - cpu0.UserTime).TotalMilliseconds;

        if (timedOut)
            return TimedOut(position, solver);

        var measuredReps = times.Count;
        return new SolveMeasurement
        {
            SolverName = solver.Name,
            PositionId = position.Id,
            Phase = position.Phase,
            IsBaseline = solver.IsBaseline,
            Found = result.Found,
            TilesPlayed = tilesPlayed,
            JokersPlayed = jokersPlayed,
            Score = result.Found ? normalized.Score : 0,
            Legal = legal,
            OverScored = overScored,
            Repetitions = measuredReps,
            TimeMsMedian = Statistics.Median(times),
            TimeMsP95 = Statistics.Percentile(times, 95),
            TimeMsMin = times.Min(),
            TimeMsMean = Statistics.Mean(times),
            AllocBytesPerSolve = measuredReps > 0 ? Math.Max(0, allocTotal) / measuredReps : 0,
            Gc0 = Math.Max(0, GC.CollectionCount(0) - gc0),
            Gc1 = Math.Max(0, GC.CollectionCount(1) - gc1),
            Gc2 = Math.Max(0, GC.CollectionCount(2) - gc2),
            CpuMsTotal = Math.Max(0, cpuTotal),
            CpuMsUser = Math.Max(0, userTotal),
            WallMsTotal = wall.Elapsed.TotalMilliseconds
        };
    }

    /// <summary>
    /// Runs the baseline (DynamicSolver) once for a position and returns its optimum signature.
    /// Used by the comparer as the optimality oracle, independent of the selected solvers.
    /// </summary>
    public ReferenceBest Baseline(BenchPosition position)
    {
        var (result, timedOut) = RunOnce(position, SolverRegistry.Baseline(position.Phase));
        return timedOut ? ReferenceBest.NotFound : ReferenceBest.From(result);
    }

    /// <summary>Single solve guarded by a cancellation-token timeout. Never throws, never blocks forever.</summary>
    private (SolverResult Result, bool TimedOut) RunOnce(BenchPosition position, SolverDescriptor solver)
    {
        using var cts = new CancellationTokenSource(_options.Timeout);
        try
        {
            var instance = solver.Create(position);
            var result = instance.SearchSolution(cts.Token);
            return (result, cts.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            return (SolverResult.Invalid(solver.Name), true);
        }
        catch
        {
            // A crash in a solver is reported as "not found", never propagated.
            return (SolverResult.Invalid(solver.Name), cts.IsCancellationRequested);
        }
    }

    private static SolveMeasurement TimedOut(BenchPosition position, SolverDescriptor solver) => new()
    {
        SolverName = solver.Name,
        PositionId = position.Id,
        Phase = position.Phase,
        IsBaseline = solver.IsBaseline,
        TimedOut = true,
        Found = false,
        Legal = false,
        Repetitions = 0
    };

    private static void CleanGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
