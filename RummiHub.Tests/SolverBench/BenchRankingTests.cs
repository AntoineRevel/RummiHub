using FluentAssertions;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Model;

namespace RummiHub.Tests.SolverBench;

public class BenchRankingTests
{
    private static SolverSummary Summary(
        string name, double median, int illegal = 0, int divergences = 0, int timeouts = 0,
        bool baseline = false, long alloc = 1000) => new()
    {
        SolverName = name,
        Phase = BenchPhase.DuringGame,
        IsBaseline = baseline,
        Positions = 10,
        Found = 10,
        TimeMsMedian = median,
        AllocBytesMean = alloc,
        Illegal = illegal,
        Divergences = divergences,
        Timeouts = timeouts
    };

    [Fact]
    public void Best_IsFastestQualifiedSolver()
    {
        var ranks = BenchRanking.ForPhase(
        [
            Summary("Clean-slow", 20, baseline: true),
            Summary("Clean-fast", 8),
            Summary("Faster-but-diverges", 3, divergences: 1)
        ], BenchPhase.DuringGame);

        ranks.Single(r => r.IsBest).SolverName.Should().Be("Clean-fast");
    }

    [Fact]
    public void FastMedianButTimingOut_DoesNotWin()
    {
        // GraphSolver-style trap: a tiny median because the hard positions timed out.
        var ranks = BenchRanking.ForPhase(
        [
            Summary("Graph-ish", 0.2, timeouts: 8),
            Summary("Dynamic", 20, baseline: true)
        ], BenchPhase.DuringGame);

        ranks.Single(r => r.IsBest).SolverName.Should().Be("Dynamic", "a solver that times out is not complete");
        ranks.First().SolverName.Should().Be("Dynamic", "qualified solvers rank above unqualified ones");
    }

    [Fact]
    public void Verdict_DescribesIssues()
    {
        var ranks = BenchRanking.ForPhase(
        [
            Summary("Good", 5),
            Summary("Bad", 1, divergences: 2, timeouts: 3)
        ], BenchPhase.DuringGame);

        ranks.Single(r => r.SolverName == "Good").Verdict.Should().Be("clean");
        ranks.Single(r => r.SolverName == "Bad").Verdict.Should().Contain("2 divergences").And.Contain("3 timeouts");
    }

    [Fact]
    public void SpeedupVsBaseline_IsRelativeToTheBaselineMedian()
    {
        var ranks = BenchRanking.ForPhase(
        [
            Summary("Dynamic", 20, baseline: true),
            Summary("Twice-as-fast", 10)
        ], BenchPhase.DuringGame);

        ranks.Single(r => r.SolverName == "Twice-as-fast").SpeedupVsBaseline.Should().BeApproximately(2.0, 0.001);
        ranks.Single(r => r.SolverName == "Dynamic").SpeedupVsBaseline.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void SpeedScore_IsOneForTheFastestQualified_AndZeroForUnqualified()
    {
        var ranks = BenchRanking.ForPhase(
        [
            Summary("Fast", 5),
            Summary("Slow", 10, baseline: true),
            Summary("Illegal", 1, illegal: 1)
        ], BenchPhase.DuringGame);

        ranks.Single(r => r.SolverName == "Fast").SpeedScore.Should().BeApproximately(1.0, 0.001);
        ranks.Single(r => r.SolverName == "Slow").SpeedScore.Should().BeApproximately(0.5, 0.001);
        ranks.Single(r => r.SolverName == "Illegal").SpeedScore.Should().Be(0);
    }

    [Fact]
    public void Best_IsNull_WhenNoSolverIsComplete()
    {
        BenchRanking.Best([Summary("OnlyOne", 5, timeouts: 1)], BenchPhase.DuringGame)
            .Should().BeNull();
    }
}
