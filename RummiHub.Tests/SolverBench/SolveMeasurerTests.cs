using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.SolverBench.Measure;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Solvers;

namespace RummiHub.Tests.SolverBench;

public class SolveMeasurerTests
{
    private static BenchPosition FirstPlay(params Tile[] hand) => new()
    {
        Id = "test-first",
        HasPlayed = false,
        Board = new Solution(),
        Hand = hand.ToList()
    };

    private static BenchPosition DuringGame(Solution board, params Tile[] hand) => new()
    {
        Id = "test-during",
        HasPlayed = true,
        Board = board,
        Hand = hand.ToList()
    };

    private static SolverDescriptor First(string name) => SolverRegistry.FirstPlay.Single(s => s.Name == name);
    private static SolverDescriptor During(string name) => SolverRegistry.DuringGame.Single(s => s.Name == name);

    [Fact]
    public void Measure_FoundMove_ReportsLegalConsistentMetrics()
    {
        // [10B,11B,12B] is a valid opening run scoring 33.
        var position = FirstPlay(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));
        var measurer = new SolveMeasurer(new MeasureOptions { Repetitions = 3 });

        var m = measurer.Measure(position, First("DynamicSolver"));

        m.Found.Should().BeTrue();
        m.Legal.Should().BeTrue();
        m.TimedOut.Should().BeFalse();
        m.Score.Should().BeGreaterThanOrEqualTo(30);
        m.RackTilesPlayed.Should().Be(m.TilesPlayed + m.JokersPlayed);
        m.TilesPlayed.Should().Be(3);

        // Measurement honesty: every captured metric is non-negative and well-formed.
        m.Repetitions.Should().Be(3);
        m.TimeMsMedian.Should().BeGreaterThanOrEqualTo(0);
        m.TimeMsP95.Should().BeGreaterThanOrEqualTo(m.TimeMsMin);
        m.AllocBytesPerSolve.Should().BeGreaterThanOrEqualTo(0);
        m.Gc0.Should().BeGreaterThanOrEqualTo(0);
        m.CpuMsTotal.Should().BeGreaterThanOrEqualTo(0);
        m.WallMsTotal.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Measure_NoMove_IsNotFound_AndNotIllegal()
    {
        // Distinct values and colors → no run, no group.
        var position = FirstPlay(new Tile(1, TileColor.Blue), new Tile(3, TileColor.Red), new Tile(5, TileColor.Black));

        var m = new SolveMeasurer(new MeasureOptions { Repetitions = 2 }).Measure(position, First("DynamicSolver"));

        m.Found.Should().BeFalse();
        m.Legal.Should().BeTrue("a 'not found' result claims no move, so it is not illegal");
        m.RackTilesPlayed.Should().Be(0);
    }

    [Fact]
    public void Measure_AllocationDelta_IsNeverNegative()
    {
        var position = FirstPlay(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

        foreach (var solver in SolverRegistry.FirstPlay)
        {
            var m = new SolveMeasurer(new MeasureOptions { Repetitions = 2 }).Measure(position, solver);
            m.AllocBytesPerSolve.Should().BeGreaterThanOrEqualTo(0, $"{solver.Name} allocation delta must be ≥ 0");
            m.CpuMsTotal.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void Measure_HeavyPosition_WithTinyTimeout_RecordsTimeout()
    {
        // A large board + full rack makes GraphSolver's exponential search hopeless within 1 ms.
        var board = new Solution { IsValid = true };
        foreach (var color in new[] { TileColor.Blue, TileColor.Red, TileColor.Black, TileColor.Mango })
            board.AddRun(new ValidSet(Enumerable.Range(1, 5).Select(v => new Tile(v, color)).ToArray()));

        var hand = new[]
        {
            new Tile(6, TileColor.Blue), new Tile(7, TileColor.Blue), new Tile(8, TileColor.Blue),
            new Tile(6, TileColor.Red), new Tile(7, TileColor.Red), new Tile(8, TileColor.Red),
            new Tile(6, TileColor.Black), new Tile(7, TileColor.Black), new Tile(8, TileColor.Black),
            new Tile(6, TileColor.Mango), new Tile(7, TileColor.Mango), new Tile(8, TileColor.Mango),
            new Tile(9, TileColor.Blue), new Tile(9, TileColor.Red)
        };

        var position = DuringGame(board, hand);
        var measurer = new SolveMeasurer(new MeasureOptions { Repetitions = 1, Timeout = TimeSpan.FromMilliseconds(1) });

        var m = measurer.Measure(position, During("GraphSolver"));

        m.TimedOut.Should().BeTrue();
        m.Legal.Should().BeFalse("a timeout produced no validated move");
    }

    [Fact]
    public void Baseline_OnFirstPlay_MatchesDynamicSolverResult()
    {
        var position = FirstPlay(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

        var baseline = new SolveMeasurer().Baseline(position);

        baseline.Found.Should().BeTrue();
        baseline.RackTilesPlayed.Should().Be(3);
    }
}
