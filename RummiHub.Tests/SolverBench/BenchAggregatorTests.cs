using FluentAssertions;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;

namespace RummiHub.Tests.SolverBench;

public class BenchAggregatorTests
{
    private static SolveMeasurement Row(
        string solver, double medianMs, bool legal = true, bool matchesOpt = true,
        bool timedOut = false, string positionId = "p", long alloc = 1000) => new()
    {
        SolverName = solver,
        PositionId = positionId,
        Phase = BenchPhase.DuringGame,
        Found = !timedOut,
        Legal = legal,
        MatchesOptimum = matchesOpt,
        TimedOut = timedOut,
        Repetitions = timedOut ? 0 : 3,
        TimeMsMedian = medianMs,
        AllocBytesPerSolve = alloc
    };

    [Fact]
    public void Summarize_RanksByMedianTime_FastestFirst()
    {
        var rows = new[]
        {
            Row("Slow", 30, positionId: "p1"),
            Row("Fast", 5, positionId: "p1"),
            Row("Medium", 12, positionId: "p1")
        };

        var summaries = BenchAggregator.Summarize(rows);

        summaries.Select(s => s.SolverName).Should().Equal("Fast", "Medium", "Slow");
    }

    [Fact]
    public void Summarize_CountsIllegalDivergenceAndTimeouts_Independently()
    {
        var rows = new[]
        {
            Row("S", 1, legal: true, matchesOpt: true, positionId: "p1"),
            Row("S", 1, legal: false, matchesOpt: true, positionId: "p2"),   // illegal only
            Row("S", 1, legal: true, matchesOpt: false, positionId: "p3"),   // divergence only
            Row("S", 1, timedOut: true, positionId: "p4")                    // timeout only
        };

        var summary = BenchAggregator.Summarize(rows).Single();

        summary.Positions.Should().Be(4);
        summary.Illegal.Should().Be(1);
        summary.Divergences.Should().Be(1);
        summary.Timeouts.Should().Be(1);
    }

    [Fact]
    public void Summarize_ExcludesTimeouts_FromIllegalAndDivergenceCounts()
    {
        var rows = new[] { Row("S", 1, legal: false, matchesOpt: false, timedOut: true) };

        var summary = BenchAggregator.Summarize(rows).Single();

        summary.Illegal.Should().Be(0);
        summary.Divergences.Should().Be(0);
        summary.Timeouts.Should().Be(1);
    }

    [Fact]
    public void Summarize_SinksFullyTimedOutSolvers_BelowCompletedOnes()
    {
        var rows = new[]
        {
            Row("AllTimeout", 0, timedOut: true, positionId: "p1"),
            Row("Slow", 100, positionId: "p1")
        };

        var summaries = BenchAggregator.Summarize(rows);

        summaries.First().SolverName.Should().Be("Slow", "a solver that completed ranks above one that only timed out");
    }

    [Fact]
    public void Summarize_AveragesAllocationsOverCompletedRows()
    {
        var rows = new[]
        {
            Row("S", 1, positionId: "p1", alloc: 1000),
            Row("S", 1, positionId: "p2", alloc: 3000)
        };

        BenchAggregator.Summarize(rows).Single().AllocBytesMean.Should().Be(2000);
    }
}
