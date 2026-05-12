using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Combinations;
using RummiHub.Solver.Worker.Solver.Combinations.First;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;
using RummiHub.Solver.Worker.Solver.Incremental;
using RummiHub.Solver.Worker.Strategies;

namespace RummiHub.Tests.Solver;

// ── Helpers ───────────────────────────────────────────────────────────────────

file static class Sets
{
    public static Set Of(params Tile[] tiles) => new(tiles.ToList());
    public static Set Empty() => new();
}

// ── First-play solver tests ───────────────────────────────────────────────────
// Covers GraphFirstSolver, IncrementalFirstBaseSolver, CombinationsFirstSolver.
// All three share the same contract: given a rack (no board), find a play
// scoring ≥ ISolver.MinScore (30).

public class FirstPlaySolverTests
{
    private static ISolver CreateSolver(string name, Set rack) => name switch
    {
        "GraphFirstSolver" => GraphFirstSolver.Create(rack),
        "IncrementalFirstBaseSolver" => IncrementalFirstBaseSolver.Create(rack),
        "CombinationsFirstSolver" => CombinationsFirstSolver.Create(rack),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static TheoryData<string> SolverNames => new()
    {
        "GraphFirstSolver",
        "IncrementalFirstBaseSolver",
        "CombinationsFirstSolver",
    };

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void ValidRun_ScoreAboveMinScore_ReturnsFound(string solverName)
    {
        // [10B, 11B, 12B] → run, score = 33 ≥ 30
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solverName} should find [10B,11B,12B] run (score=33)");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void ValidGroup_AtExactlyMinScore_ReturnsFound(string solverName)
    {
        // [10B, 10R, 10Bl] → group, score = 30 = MinScore
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(10, TileColor.Red), new Tile(10, TileColor.Black));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solverName} should find [10B,10R,10Bl] group (score=30=MinScore)");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void ValidRun_WithJokerFillingGap_ReturnsFound(string solverName)
    {
        // [10B, 11B, 13B] + joker → [10B, 11B, joker(12B), 13B] run, score = 10+11+13 = 34 ≥ 30
        // Joker is required: without it, [10B, 11B, 13B] has a gap at 12B and cannot form a run.
        var rack = Sets.Of(
            new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue),
            new Tile(13, TileColor.Blue), new Tile(isJoker: true));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solverName} should fill gap with joker to form run scoring 34");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void RunBelowMinScore_ReturnsNotFound(string solverName)
    {
        // [1B, 2B, 3B] → valid run but score = 6 < 30
        var rack = Sets.Of(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeFalse(because: $"{solverName} should reject [1B,2B,3B] run (score=6<30)");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void GroupBelowMinScore_ReturnsNotFound(string solverName)
    {
        // [9B, 9R, 9Bl] → valid group but score = 27 < 30
        var rack = Sets.Of(new Tile(9, TileColor.Blue), new Tile(9, TileColor.Red), new Tile(9, TileColor.Black));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeFalse(because: $"{solverName} should reject [9B,9R,9Bl] group (score=27<30)");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void NoValidSet_ReturnsNotFound(string solverName)
    {
        // 1B, 3R, 5Bl: different values AND different colors → no run, no group
        var rack = Sets.Of(new Tile(1, TileColor.Blue), new Tile(3, TileColor.Red), new Tile(5, TileColor.Black));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeFalse(because: $"{solverName} should reject tiles with no valid set");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void EmptyRack_ReturnsNotFound(string solverName)
    {
        var result = CreateSolver(solverName, Sets.Empty()).SearchSolution();

        result.Found.Should().BeFalse(because: $"{solverName} should return not found for empty rack");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void WhenFound_SolutionIsValid(string solverName)
    {
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(10, TileColor.Red), new Tile(10, TileColor.Black));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeTrue();
        result.BestSolution.IsValid.Should().BeTrue(because: "Found=true must always pair with a valid Solution");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void WhenFound_TilesToPlay_IsNotEmpty(string solverName)
    {
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeTrue();
        result.TilesToPlay.Should().NotBeEmpty(because: "at least one tile must be played when Found=true");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void WhenFound_TilesToPlay_AreSubsetOfRack(string solverName)
    {
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

        var result = CreateSolver(solverName, rack).SearchSolution();

        result.Found.Should().BeTrue();
        result.TilesToPlay.Should().OnlyContain(
            t => t.IsJoker || rack.Tiles.Contains(t),
            because: "tiles played must come from the rack");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void Cancellation_DoesNotThrow(string solverName)
    {
        var rack = Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => CreateSolver(solverName, rack).SearchSolution(cts.Token);

        act.Should().NotThrow(because: $"{solverName} should handle pre-cancelled tokens gracefully");
    }
}

// ── BinaryFirstBaseSolver tests ───────────────────────────────────────────────
// BinaryFirstBaseSolver has different semantics from the other first-play
// solvers: it requires ALL provided tiles (plus jokers) to be placed into
// valid sets. It is used internally by CombinationsFirstSolver.

public class BinaryFirstBaseSolverTests
{
    private static BinaryFirstBaseSolver Create(Tile[] sortedTiles, int jokers = 0)
        => new(sortedTiles, jokers);

    [Fact]
    public void AllTilesFormValidRun_AboveMinScore_ReturnsFound()
    {
        // [10B, 11B, 12B] all fit in one run, score = 33 ≥ 30
        var tiles = new[] { new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue) };

        var result = Create(tiles).SearchSolution();

        result.Found.Should().BeTrue();
        result.BestSolution.IsValid.Should().BeTrue();
    }

    [Fact]
    public void AllTilesFormValidRun_BelowMinScore_ReturnsNotFound()
    {
        // [1B, 2B, 3B] form a valid run but score = 6 < 30
        var tiles = new[] { new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue) };

        var result = Create(tiles).SearchSolution();

        result.Found.Should().BeFalse(because: "score 6 is below MinScore 30");
    }

    [Fact]
    public void NotAllTilesCanBeArranged_ReturnsNotFound()
    {
        // [7R, 10B, 11B, 12B]: 7R cannot join any set with the blue run
        var tiles = new[]
        {
            new Tile(7, TileColor.Red),
            new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue)
        };

        var result = Create(tiles).SearchSolution();

        result.Found.Should().BeFalse(because: "7R cannot be arranged with [10B,11B,12B]");
    }

    [Fact]
    public void WithJoker_FillingGapToReachMinScore_ReturnsFound()
    {
        // [10B, 11B, 13B] + 1 joker → [10B, 11B, joker(12B), 13B] run, score = 34 ≥ 30
        var tiles = new[] { new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(13, TileColor.Blue) };

        var result = Create(tiles, jokers: 1).SearchSolution();

        result.Found.Should().BeTrue(because: "joker fills gap at 12B to form a run scoring 34");
    }
}

// ── During-game solver tests ──────────────────────────────────────────────────
// Covers GraphSolver, IncrementalComplexSolver, CombinationsSolver, and
// ParallelCombinationsSolver. Contract: given a board and a rack, find a
// rearrangement that uses ALL board tiles and plays at least one rack tile.

public class DuringGameSolverTests
{
    private static ISolver CreateSolver(string name, Set board, Set rack) => name switch
    {
        "GraphSolver" => GraphSolver.Create(board, rack),
        "IncrementalComplexSolver" => IncrementalComplexSolver.Create(board, rack),
        "CombinationsSolver" => CombinationsSolver.Create(board, rack),
        "ParallelCombinationsSolver" => ParallelCombinationsSolver.Create(board, rack),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    public static TheoryData<string> SolverNames => new()
    {
        "GraphSolver",
        "IncrementalComplexSolver",
        "CombinationsSolver",
        "ParallelCombinationsSolver",
    };

    private static Set BoardRun() =>
        Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void ExtendBoardRun_ReturnsFound(string solverName)
    {
        // Board: [10B, 11B, 12B] + rack: [13B] → extend the run to [10B..13B]
        var result = CreateSolver(solverName, BoardRun(), Sets.Of(new Tile(13, TileColor.Blue))).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solverName} should extend [10B,11B,12B] with 13B");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void ExtendBoardRun_TilesToPlay_ContainsRackTile(string solverName)
    {
        var rackTile = new Tile(13, TileColor.Blue);
        var result = CreateSolver(solverName, BoardRun(), Sets.Of(rackTile)).SearchSolution();

        result.Found.Should().BeTrue();
        result.TilesToPlay.Should().Contain(rackTile, because: $"{solverName} must report 13B as the played tile");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void RackTileCannotBeIncorporated_ReturnsNotFound(string solverName)
    {
        // 7R cannot form any set with board tiles [10B, 11B, 12B]:
        // - not part of a run (different color)
        // - not part of a group (no other value-7 tiles present)
        var result = CreateSolver(solverName, BoardRun(), Sets.Of(new Tile(7, TileColor.Red))).SearchSolution();

        result.Found.Should().BeFalse(because: $"{solverName} should not find a play when 7R cannot join any set");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void EmptyRack_ReturnsNotFound(string solverName)
    {
        // No rack tiles → nothing to play → must be invalid even if board is valid
        var result = CreateSolver(solverName, BoardRun(), Sets.Empty()).SearchSolution();

        result.Found.Should().BeFalse(because: $"{solverName} requires at least one rack tile to be played");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void RackFormsSeparateRun_ReturnsFound(string solverName)
    {
        // Board: [10B, 11B, 12B] + rack: [4B, 5B, 6B]
        // Rack tiles form their own independent run; board tiles form theirs.
        var rack = Sets.Of(new Tile(4, TileColor.Blue), new Tile(5, TileColor.Blue), new Tile(6, TileColor.Blue));

        var result = CreateSolver(solverName, BoardRun(), rack).SearchSolution();

        result.Found.Should().BeTrue(because: $"{solverName} should find two independent runs");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void WhenFound_SolutionIsValid(string solverName)
    {
        var result = CreateSolver(solverName, BoardRun(), Sets.Of(new Tile(13, TileColor.Blue))).SearchSolution();

        result.Found.Should().BeTrue();
        result.BestSolution.IsValid.Should().BeTrue(because: "Found=true must pair with a valid Solution");
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void WhenFound_AllBoardTilesAppearInSolution(string solverName)
    {
        // The solver must reorganize tiles such that every board tile is in some set.
        var b10 = new Tile(10, TileColor.Blue);
        var b11 = new Tile(11, TileColor.Blue);
        var b12 = new Tile(12, TileColor.Blue);
        var board = Sets.Of(b10, b11, b12);

        var result = CreateSolver(solverName, board, Sets.Of(new Tile(13, TileColor.Blue))).SearchSolution();

        result.Found.Should().BeTrue();
        var allSolutionTiles = result.BestSolution.ToSet().Tiles;
        allSolutionTiles.Should().Contain(b10).And.Contain(b11).And.Contain(b12);
    }

    [Theory]
    [MemberData(nameof(SolverNames))]
    public void Cancellation_DoesNotThrow(string solverName)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => CreateSolver(solverName, BoardRun(), Sets.Of(new Tile(13, TileColor.Blue)))
            .SearchSolution(cts.Token);

        act.Should().NotThrow(because: $"{solverName} should handle pre-cancelled tokens gracefully");
    }
}

// ── BinaryBaseSolver tests ────────────────────────────────────────────────────
// BinaryBaseSolver requires ALL provided tiles to fit into valid sets and is
// used internally by CombinationsSolver. TilesToPlay and JokerToPlay must be
// supplied by the caller to identify which tiles came from the rack.

public class BinaryBaseSolverTests
{
    [Fact]
    public void AllTilesFormTwoRuns_ReturnsFound()
    {
        // Board [10B,11B,12B] + rack [13B] → single run [10B..13B]: all tiles used
        var rackTile = new Tile(13, TileColor.Blue);
        var allTiles = new[]
        {
            new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue),
            new Tile(12, TileColor.Blue), rackTile
        };
        var solver = new BinaryBaseSolver(allTiles, jokers: 0)
        {
            TilesToPlay = [rackTile],
            JokerToPlay = 0
        };

        var result = solver.SearchSolution();

        result.Found.Should().BeTrue();
        result.TilesToPlay.Should().Contain(rackTile);
    }

    [Fact]
    public void TilesCannotAllBeArranged_ReturnsNotFound()
    {
        // [7R, 10B, 11B, 12B]: 7R blocks a complete valid arrangement
        var rackTile = new Tile(7, TileColor.Red);
        var allTiles = new[]
        {
            rackTile,
            new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue)
        };
        var solver = new BinaryBaseSolver(allTiles, jokers: 0)
        {
            TilesToPlay = [rackTile],
            JokerToPlay = 0
        };

        var result = solver.SearchSolution();

        result.Found.Should().BeFalse();
    }

    [Fact]
    public void WithJoker_AllTilesArranged_ReturnsFound()
    {
        // Board [10B, 12B] (gap at 11B) + 1 joker → [10B, joker(11B), 12B] run: all tiles used
        var rackTile = new Tile(12, TileColor.Blue);
        var allTiles = new[] { new Tile(10, TileColor.Blue), rackTile };
        var solver = new BinaryBaseSolver(allTiles, jokers: 1)
        {
            TilesToPlay = [rackTile],
            JokerToPlay = 0
        };

        var result = solver.SearchSolution();

        result.Found.Should().BeTrue(because: "joker fills the gap at 11B to complete a run");
    }
}

// ── Strategy tests ─────────────────────────────────────────────────────────────
// Tests for the two IStrategy implementations (GraphStrategy, ParallelStrategy)
// that are wired into the SolverWorker and selected based on game state.

public class StrategyTests
{
    private static readonly Set ValidFirstPlayRack =
        Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

    private static readonly Set BelowMinScoreRack =
        Sets.Of(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue));

    private static readonly Set BoardRun =
        Sets.Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));

    private static readonly Set ExtendingRackTile = Sets.Of(new Tile(13, TileColor.Blue));

    [Fact]
    public async Task GraphStrategy_FirstPlay_ValidRack_ReturnsFound()
    {
        var result = await new GraphStrategy().GetSolverResult(Sets.Empty(), ValidFirstPlayRack, hasPlayed: false);

        result.Found.Should().BeTrue();
    }

    [Fact]
    public async Task GraphStrategy_FirstPlay_BelowMinScore_ReturnsNotFound()
    {
        var result = await new GraphStrategy().GetSolverResult(Sets.Empty(), BelowMinScoreRack, hasPlayed: false);

        result.Found.Should().BeFalse();
    }

    [Fact]
    public async Task GraphStrategy_DuringGame_ExtendBoardRun_ReturnsFound()
    {
        var result = await new GraphStrategy().GetSolverResult(BoardRun, ExtendingRackTile, hasPlayed: true);

        result.Found.Should().BeTrue();
    }

    [Fact]
    public async Task ParallelStrategy_FirstPlay_ValidRack_ReturnsFound()
    {
        var result = await new ParallelStrategy().GetSolverResult(Sets.Empty(), ValidFirstPlayRack, hasPlayed: false);

        result.Found.Should().BeTrue();
    }

    [Fact]
    public async Task ParallelStrategy_FirstPlay_BelowMinScore_ReturnsNotFound()
    {
        var result = await new ParallelStrategy().GetSolverResult(Sets.Empty(), BelowMinScoreRack, hasPlayed: false);

        result.Found.Should().BeFalse();
    }

    [Fact]
    public async Task ParallelStrategy_DuringGame_ExtendBoardRun_ReturnsFound()
    {
        var result = await new ParallelStrategy().GetSolverResult(BoardRun, ExtendingRackTile, hasPlayed: true);

        result.Found.Should().BeTrue();
    }

    [Fact]
    public async Task BothStrategies_AgreeOnFoundResult_ForSameInput()
    {
        // Regression guard: the two strategies use different algorithms but must
        // agree on whether a valid play exists.
        var graphResult = await new GraphStrategy().GetSolverResult(BoardRun, ExtendingRackTile, hasPlayed: true);
        var parallelResult = await new ParallelStrategy().GetSolverResult(
            new Set(BoardRun), new Set(ExtendingRackTile), hasPlayed: true);

        graphResult.Found.Should().Be(parallelResult.Found,
            because: "GraphStrategy and ParallelStrategy must agree on whether a solution exists");
    }
}
