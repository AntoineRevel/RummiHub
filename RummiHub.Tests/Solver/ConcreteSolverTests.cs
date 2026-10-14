using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Combinations;
using RummiHub.Solver.Worker.Solver.Combinations.First;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;
using RummiHub.Solver.Worker.Solver.Incremental;
using RummiHub.Solver.Worker.Strategies;

namespace RummiHub.Tests.Solver;

file static class Sets
{
    public static Set Of(params Tile[] tiles) => new(tiles.ToList());
    public static Set Empty() => new();
}

// ── First-play solvers ────────────────────────────────────────────────────────
// GraphFirstSolver, IncrementalFirstBaseSolver and CombinationsFirstSolver share
// one contract: given a rack (no board), find a play scoring ≥ ISolver.MinScore (30).

public class FirstPlaySolverTests
{
    private static ISolver Create(string name, Set rack) => name switch
    {
        "GraphFirstSolver" => GraphFirstSolver.Create(rack),
        "IncrementalFirstBaseSolver" => IncrementalFirstBaseSolver.Create(rack),
        "CombinationsFirstSolver" => CombinationsFirstSolver.Create(rack),
        "DynamicSolver" => DynamicSolver.CreateFirst(rack),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static TheoryData<string> Solvers => new()
    {
        "GraphFirstSolver", "IncrementalFirstBaseSolver", "CombinationsFirstSolver", "DynamicSolver"
    };

    [Theory]
    [MemberData(nameof(Solvers))]
    public void ValidRunAboveMinScore_IsFound_WithConsistentResult(string solver)
    {
        // [10B, 11B, 12B] → run, score = 33 ≥ 30
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

        var result = Create(solver, rack).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solver} should find the [10B,11B,12B] run (score 33)");
        result.BestSolution.IsValid.Should().BeTrue(because: "Found=true must pair with a valid Solution");
        result.TilesToPlay.Should().NotBeEmpty()
            .And.OnlyContain(t => t.IsJoker || rack.Tiles.Contains(t), "played tiles come from the rack");
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void ValidGroupAtExactlyMinScore_IsFound(string solver)
    {
        // [10B, 10R, 10Bl] → group, score = 30 = MinScore (boundary, exercises the group path)
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(10, TileColor.Red), new Tile(10, TileColor.Black));

        Create(solver, rack).SearchSolution().Found.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void JokerFillsRunGap_IsFound(string solver)
    {
        // [10B, 11B, 13B] + joker → [10B, 11B, joker(12B), 13B], score = 34. Joker is required.
        var rack = Sets.Of(
            new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue),
            new Tile(13, TileColor.Blue), new Tile(isJoker: true));

        Create(solver, rack).SearchSolution().Found.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void RunBelowMinScore_IsNotFound(string solver)
    {
        // [1B, 2B, 3B] → valid run, but score = 6 < 30
        var rack = Sets.Of(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue));

        Create(solver, rack).SearchSolution().Found.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void NoValidSet_IsNotFound(string solver)
    {
        // Distinct values AND colors → no run, no group
        var rack = Sets.Of(new Tile(1, TileColor.Blue), new Tile(3, TileColor.Red), new Tile(5, TileColor.Black));

        Create(solver, rack).SearchSolution().Found.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void EmptyRack_IsNotFound(string solver)
    {
        Create(solver, Sets.Empty()).SearchSolution().Found.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void PreCancelledToken_DoesNotThrow(string solver)
    {
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Create(solver, rack).SearchSolution(cts.Token);

        act.Should().NotThrow();
    }
}

// ── During-game solvers ───────────────────────────────────────────────────────
// GraphSolver, IncrementalComplexSolver, CombinationsSolver and
// ParallelCombinationsSolver share one contract: given a board and a rack, find a
// rearrangement that uses ALL board tiles and plays at least one rack tile.

public class DuringGameSolverTests
{
    private static ISolver Create(string name, Set board, Set rack) => name switch
    {
        "GraphSolver" => GraphSolver.Create(board, rack),
        "IncrementalComplexSolver" => IncrementalComplexSolver.Create(board, rack),
        "CombinationsSolver" => CombinationsSolver.Create(board, rack),
        "ParallelCombinationsSolver" => ParallelCombinationsSolver.Create(board, rack),
        "DynamicSolver" => DynamicSolver.Create(board, rack),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static TheoryData<string> Solvers => new()
    {
        "GraphSolver", "IncrementalComplexSolver", "CombinationsSolver", "ParallelCombinationsSolver", "DynamicSolver"
    };

    private static Set BoardRun() =>
        Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

    [Theory]
    [MemberData(nameof(Solvers))]
    public void ExtendBoardRun_IsFound_AndPreservesBoard(string solver)
    {
        // Board [10B, 11B, 12B] + rack [13B] → extend the run to [10B..13B]
        var rackTile = new Tile(13, TileColor.Blue);
        var board = BoardRun();

        var result = Create(solver, board, Sets.Of(rackTile)).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solver} should extend the run with 13B");
        result.BestSolution.IsValid.Should().BeTrue();
        result.TilesToPlay.Should().Contain(rackTile, "the rack tile must be reported as played");
        result.BestSolution.ToSet().Tiles.Should()
            .Contain(board.Tiles, "every board tile must remain in the solution");
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void RackFormsSeparateRun_IsFound(string solver)
    {
        // Board [10B, 11B, 12B] + rack [4B, 5B, 6B] → two independent runs
        var rack = Sets.Of(new Tile(4, TileColor.Blue), new Tile(5, TileColor.Blue), new Tile(6, TileColor.Blue));

        Create(solver, BoardRun(), rack).SearchSolution().Found.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void RackTileCannotBeIncorporated_IsNotFound(string solver)
    {
        // 7R joins no set with [10B, 11B, 12B]: wrong color for the run, no other 7s for a group
        Create(solver, BoardRun(), Sets.Of(new Tile(7, TileColor.Red))).SearchSolution()
            .Found.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Solvers))]
    public void PreCancelledToken_DoesNotThrow(string solver)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Create(solver, BoardRun(), Sets.Of(new Tile(13, TileColor.Blue))).SearchSolution(cts.Token);

        act.Should().NotThrow();
    }
}

// ── Strategies ─────────────────────────────────────────────────────────────────
// The two IStrategy implementations are the production entry points wired into
// SolverWorker (GraphStrategy is the active default).

public class StrategyTests
{
    private static Set ValidFirstPlay() =>
        Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

    private static Set BelowMinScore() =>
        Sets.Of(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue));

    // Three 9s: a legal group worth 27 — valid, but short of the 30-point first-play threshold.
    private static Set BelowMinScoreValidGroup() =>
        Sets.Of(new Tile(9, TileColor.Blue), new Tile(9, TileColor.Red), new Tile(9, TileColor.Black));

    private static Set BoardRun() =>
        Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

    private static Set ExtendingTile() => Sets.Of(new Tile(13, TileColor.Blue));

    [Fact]
    public async Task GraphStrategy_FirstPlayValid_IsFound()
        => (await new GraphStrategy().GetSolverResult(Sets.Empty(), ValidFirstPlay(), hasPlayed: false))
            .Found.Should().BeTrue();

    [Fact]
    public async Task GraphStrategy_FirstPlayBelowMinScore_IsNotFound()
        => (await new GraphStrategy().GetSolverResult(Sets.Empty(), BelowMinScore(), hasPlayed: false))
            .Found.Should().BeFalse();

    [Fact]
    public async Task GraphStrategy_FirstPlayBelowMinScore_ReportsBestScore()
        => (await new GraphStrategy().GetSolverResult(Sets.Empty(), BelowMinScoreValidGroup(), hasPlayed: false))
            .Score.Should().Be(27, because: "the best achievable score is reported even when the move is rejected");

    [Fact]
    public async Task GraphStrategy_DuringGameExtend_IsFound()
        => (await new GraphStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true))
            .Found.Should().BeTrue();

    [Fact]
    public async Task ParallelStrategy_FirstPlayValid_IsFound()
        => (await new ParallelStrategy().GetSolverResult(Sets.Empty(), ValidFirstPlay(), hasPlayed: false))
            .Found.Should().BeTrue();

    [Fact]
    public async Task ParallelStrategy_FirstPlayBelowMinScore_IsNotFound()
        => (await new ParallelStrategy().GetSolverResult(Sets.Empty(), BelowMinScore(), hasPlayed: false))
            .Found.Should().BeFalse();

    [Fact]
    public async Task ParallelStrategy_DuringGameExtend_IsFound()
        => (await new ParallelStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true))
            .Found.Should().BeTrue();

    [Fact]
    public async Task BothStrategies_AgreeOnWhetherSolutionExists()
    {
        // Different algorithms, same contract: they must agree on Found for identical input.
        var graph = await new GraphStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true);
        var parallel = await new ParallelStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true);

        graph.Found.Should().Be(parallel.Found);
    }

    [Fact]
    public async Task DynamicStrategy_FirstPlayValid_IsFound()
        => (await new DynamicStrategy().GetSolverResult(Sets.Empty(), ValidFirstPlay(), hasPlayed: false))
            .Found.Should().BeTrue();

    [Fact]
    public async Task DynamicStrategy_FirstPlayBelowMinScore_IsNotFound()
        => (await new DynamicStrategy().GetSolverResult(Sets.Empty(), BelowMinScore(), hasPlayed: false))
            .Found.Should().BeFalse();

    [Fact]
    public async Task DynamicStrategy_FirstPlayBelowMinScore_ReportsBestScore()
    {
        // Full production path (DynamicStrategy as wired in SolverWorker): a sub-30 first play
        // must be rejected but still surface its real score so the UI shows it, not 0.
        var result = await new DynamicStrategy()
            .GetSolverResult(Sets.Empty(), BelowMinScoreValidGroup(), hasPlayed: false);

        result.Found.Should().BeFalse();
        result.Score.Should().Be(27);
    }

    [Fact]
    public async Task DynamicStrategy_DuringGameExtend_IsFound()
        => (await new DynamicStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true))
            .Found.Should().BeTrue();

    [Fact]
    public async Task DynamicStrategy_AgreesWithGraphStrategy_OnExistence()
    {
        var dynamic = await new DynamicStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true);
        var graph = await new GraphStrategy().GetSolverResult(BoardRun(), ExtendingTile(), hasPlayed: true);

        dynamic.Found.Should().Be(graph.Found);
    }
}
