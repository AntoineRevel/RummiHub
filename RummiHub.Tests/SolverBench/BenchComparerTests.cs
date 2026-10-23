using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.SolverBench;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Solvers;
using RummiHub.SolverBench.Trace;

namespace RummiHub.Tests.SolverBench;

public class BenchComparerTests
{
    private static SolveMeasurement Measurement(bool found, int tiles = 0, int jokers = 0, bool timedOut = false) => new()
    {
        SolverName = "X",
        PositionId = "p",
        Phase = BenchPhase.DuringGame,
        Found = found,
        TilesPlayed = tiles,
        JokersPlayed = jokers,
        TimedOut = timedOut
    };

    private static ReferenceBest Ref(bool found, int tiles = 0, int jokers = 0) => new()
    {
        Found = found,
        Tiles = Enumerable.Range(0, tiles).Select(_ => new Tile(1)).ToList(),
        Jokers = jokers
    };

    // ── verdict logic (deterministic, the core of "divergence vs DP" detection) ──────────

    [Fact]
    public void Verdict_DuringGame_SameRackTilesPlayed_Matches()
    {
        var (matches, _) = BenchComparer.Verdict(BenchPhase.DuringGame, Measurement(true, tiles: 4), Ref(true, tiles: 4));
        matches.Should().BeTrue();
    }

    [Fact]
    public void Verdict_DuringGame_FewerTilesThanDp_IsDivergence_WithNote()
    {
        var (matches, note) = BenchComparer.Verdict(BenchPhase.DuringGame, Measurement(true, tiles: 3), Ref(true, tiles: 4));
        matches.Should().BeFalse();
        note.Should().Contain("3").And.Contain("4");
    }

    [Fact]
    public void Verdict_FoundButDpDidNot_IsDivergence()
    {
        BenchComparer.Verdict(BenchPhase.DuringGame, Measurement(true, tiles: 2), Ref(false))
            .Matches.Should().BeFalse();
    }

    [Fact]
    public void Verdict_BothAgreeNoMove_Matches()
    {
        BenchComparer.Verdict(BenchPhase.DuringGame, Measurement(false), Ref(false))
            .Matches.Should().BeTrue();
    }

    [Fact]
    public void Verdict_FirstPlay_OnlyRequiresFoundAgreement()
    {
        // First-play solvers satisfice (any legal ≥30 opening), so differing tile counts still match.
        BenchComparer.Verdict(BenchPhase.FirstPlay, Measurement(true, tiles: 6), Ref(true, tiles: 3))
            .Matches.Should().BeTrue();
    }

    [Fact]
    public void Verdict_Timeout_IsNeverAMatch()
    {
        var (matches, note) = BenchComparer.Verdict(BenchPhase.DuringGame, Measurement(false, timedOut: true), Ref(true, tiles: 4));
        matches.Should().BeFalse();
        note.Should().Be("timeout");
    }

    // ── end-to-end ──────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<BenchPosition> Corpus() =>
        BenchFacade.TraceCorpus(12345, new GameTraceOptions { MaxPositions = 24 });

    private static MeasureOptions Fast => new() { Repetitions = 1, WarmupRuns = 1, Timeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public void Compare_BaselineOnly_IsAlwaysLegalAndOptimal()
    {
        var baseline = new[] { SolverRegistry.Baseline(BenchPhase.FirstPlay), SolverRegistry.Baseline(BenchPhase.DuringGame) };

        var result = new BenchComparer(Fast).Compare(Corpus(), baseline, "test");

        result.Rows.Should().NotBeEmpty();
        result.IllegalCount.Should().Be(0, "DynamicSolver is the validated baseline");
        result.DivergenceCount.Should().Be(0, "the baseline cannot diverge from itself");
        result.Summaries.Should().NotBeEmpty();
    }

    [Fact]
    public void Registry_ListsDynamicAndGraphOnly()
    {
        SolverRegistry.FirstPlay.Select(s => s.Name).Should().BeEquivalentTo("GraphFirstSolver", "DynamicSolver");
        SolverRegistry.DuringGame.Select(s => s.Name).Should().BeEquivalentTo("GraphSolver", "DynamicSolver");
        SolverRegistry.FirstPlay.Count(s => s.IsBaseline).Should().Be(1);
        SolverRegistry.DuringGame.Count(s => s.IsBaseline).Should().Be(1);
    }

    [Fact]
    public void Compare_ProducesDeterministicVerdicts_AcrossRuns()
    {
        var corpus = Corpus();
        var solvers = new[]
        {
            SolverRegistry.Baseline(BenchPhase.FirstPlay),
            SolverRegistry.Baseline(BenchPhase.DuringGame),
            SolverRegistry.DuringGame.Single(s => s.Name == "GraphSolver")
        };

        var first = new BenchComparer(Fast).Compare(corpus, solvers, "run1");
        var second = new BenchComparer(Fast).Compare(corpus, solvers, "run2");

        // Timings differ between runs, but the found / legal / optimum verdicts must not — this is
        // what guarantees the headless runner and the /bench UI reach the same conclusions.
        Verdicts(first).Should().BeEquivalentTo(Verdicts(second));
    }

    private static IEnumerable<(string, string, bool, bool, bool)> Verdicts(ComparisonResult r) =>
        r.Rows.Select(x => (x.PositionId, x.SolverName, x.Found, x.Legal, x.MatchesOptimum));
}
