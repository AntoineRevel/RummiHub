using AwesomeAssertions;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Trace;

namespace RummiHub.Tests.SolverBench;

public class GameTraceGeneratorTests
{
    private static GameTraceOptions Short => new() { MaxPositions = 20, Games = 1 };

    [Fact]
    public void SameSeed_ProducesIdenticalTrace()
    {
        var a = GameTraceGenerator.Generate(42, Short);
        var b = GameTraceGenerator.Generate(42, Short);

        a.Positions.Select(p => p.Id).Should().Equal(b.Positions.Select(p => p.Id));
        a.Positions.Select(p => p.HasPlayed).Should().Equal(b.Positions.Select(p => p.HasPlayed));
        a.Positions.Select(p => p.ReferenceBest.Score).Should().Equal(b.Positions.Select(p => p.ReferenceBest.Score));
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentTrace()
    {
        var a = GameTraceGenerator.Generate(1, Short);
        var b = GameTraceGenerator.Generate(2, Short);

        a.Positions.Select(p => p.Id).Should().NotEqual(b.Positions.Select(p => p.Id));
    }

    [Fact]
    public void Trace_CapturesBothPhases()
    {
        var trace = GameTraceGenerator.Generate(12345, new GameTraceOptions { MaxPositions = 40 });

        trace.FirstPlayCount.Should().BeGreaterThan(0, "the opening moves are first-play positions");
        trace.DuringGameCount.Should().BeGreaterThan(0, "once a player has opened, later moves are during-game");
    }

    [Fact]
    public void FirstPlayPositions_HaveEmptyBoard_DuringGamePositions_HaveTilesAndReference()
    {
        var trace = GameTraceGenerator.Generate(7, new GameTraceOptions { MaxPositions = 40 });

        foreach (var first in trace.Positions.Where(p => p.Phase == BenchPhase.FirstPlay))
        {
            first.Board.Sets.Should().BeEmpty();
        }

        foreach (var during in trace.Positions.Where(p => p.Phase == BenchPhase.DuringGame))
            during.Board.Sets.Count
                .Should().BeGreaterThan(0, "a during-game position has a non-empty board");
    }

    [Fact]
    public void Position_Id_IsStableContentHash()
    {
        var trace = GameTraceGenerator.Generate(99, Short);
        var position = trace.Positions.First();

        BenchPosition.ComputeId(position.Hand, position.Board, position.HasPlayed)
            .Should().Be(position.Id);
    }

    [Fact]
    public void MaxPositions_IsRespected()
    {
        var trace = GameTraceGenerator.Generate(3, new GameTraceOptions { MaxPositions = 5, Games = 5 });
        trace.Positions.Count.Should().BeLessThanOrEqualTo(5);
    }
}
