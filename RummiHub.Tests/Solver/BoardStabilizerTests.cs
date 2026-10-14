using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;

namespace RummiHub.Tests.Solver;

/// <summary>
/// The stabilizer must return the same MOVE the solver found (same tiles played) while
/// keeping every set not involved in the move literally identical. Frozen sets are asserted
/// by REFERENCE: only the stabilizer reuses the previous board's ValidSet instances, so a
/// reference match proves the set was frozen rather than accidentally rebuilt equal.
/// </summary>
public class BoardStabilizerTests
{
    private static Tile T(int value, TileColor color) => new(value, color);
    private static ValidSet VSet(params Tile[] tiles) => new(tiles);

    private static SolverResult SolveFor(Solution board, Set rack) =>
        DynamicSolver.Create(board.ToSet(), rack).SearchSolution();

    private static void ShouldBeLegalOver(SolverResult result, Solution board, Set rack) =>
        SolutionValidator.IsValid(result, board.ToSet(), rack, hasPlayed: true)
            .Should().BeTrue("the stabilized arrangement must stay a fully legal move");

    [Fact]
    public void PlayedTilesFormTheirOwnSet_BoardStaysUntouched()
    {
        var blueRun = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var redRun = VSet(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red));
        var board = new Solution { IsValid = true, Runs = [blueRun, redRun] };
        var rack = new Set([T(11, TileColor.Blue), T(11, TileColor.Red), T(11, TileColor.Black)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(3);

        var result = BoardStabilizer.Stabilize(board, solved);

        result.BestSolution.Runs.Should().HaveCount(2)
            .And.Contain(s => ReferenceEquals(s, blueRun))
            .And.Contain(s => ReferenceEquals(s, redRun));
        result.BestSolution.Groups.Should().ContainSingle()
            .Which.Tiles.Should().AllSatisfy(t => t.Value.Should().Be(11));
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void ExtendingARun_OnlyBreaksThatRun()
    {
        var blueRun = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var redRun = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var nines = VSet(T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Mango));
        var board = new Solution { IsValid = true, Runs = [blueRun, redRun], Groups = [nines] };
        var rack = new Set([T(4, TileColor.Blue)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().ContainSingle();

        var result = BoardStabilizer.Stabilize(board, solved);

        // The untouched sets come back as the very same instances…
        result.BestSolution.Runs.Should().Contain(s => ReferenceEquals(s, redRun));
        result.BestSolution.Groups.Should().ContainSingle()
            .Which.Should().BeSameAs(nines);
        // …and the blue run simply grew by the played tile.
        result.BestSolution.Runs.Should().HaveCount(2)
            .And.Contain(s => s.Tiles.Length == 4 && s.Tiles.All(t => t.Color == TileColor.Blue));
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void SplittingARun_LeavesTheOtherSetsFrozen()
    {
        // Playing the second copy of 8-Black splits [6..10] into [6,7,8] + [8,9,10];
        // the ones-group has nothing to do with the move and must stay frozen.
        var blackRun = VSet(T(6, TileColor.Black), T(7, TileColor.Black), T(8, TileColor.Black),
            T(9, TileColor.Black), T(10, TileColor.Black));
        var ones = VSet(T(1, TileColor.Blue), T(1, TileColor.Red), T(1, TileColor.Mango));
        var board = new Solution { IsValid = true, Runs = [blackRun], Groups = [ones] };
        var rack = new Set([T(8, TileColor.Black)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().ContainSingle();

        var result = BoardStabilizer.Stabilize(board, solved);

        result.BestSolution.Groups.Should().ContainSingle().Which.Should().BeSameAs(ones);
        result.BestSolution.Runs.Should().HaveCount(2)
            .And.AllSatisfy(r => r.Tiles.Should().HaveCount(3));
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void RepairsAChurnedArrangement_BackToTheExistingSets()
    {
        // The move only completes the nines group, but the (hand-crafted) solver output
        // gratuitously merged the two blue runs into one. Same tiles, needless churn —
        // the stabilizer must restore both original runs and just extend the group.
        var lowBlue = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var highBlue = VSet(T(4, TileColor.Blue), T(5, TileColor.Blue), T(6, TileColor.Blue));
        var nines = VSet(T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Mango));
        var board = new Solution { IsValid = true, Runs = [lowBlue, highBlue], Groups = [nines] };

        var churned = new Solution { IsValid = true };
        churned.AddRun(VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue),
            T(4, TileColor.Blue), T(5, TileColor.Blue), T(6, TileColor.Blue)));
        churned.AddGroup(VSet(T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Mango),
            T(9, TileColor.Black)));
        var solved = SolverResult.FromSolution(
            "test", churned, [T(9, TileColor.Black)], jokersToPlay: 0, score: 57);

        var result = BoardStabilizer.Stabilize(board, solved);

        result.BestSolution.Runs.Should().HaveCount(2)
            .And.Contain(s => ReferenceEquals(s, lowBlue))
            .And.Contain(s => ReferenceEquals(s, highBlue));
        result.BestSolution.Groups.Should().ContainSingle()
            .Which.Tiles.Should().HaveCount(4);
        result.TilesToPlay.Should().BeEquivalentTo(new[] { T(9, TileColor.Black) });
        ShouldBeLegalOver(result, board, new Set([T(9, TileColor.Black)]));
    }

    [Fact]
    public void MoveNeedingTheWholeTable_FallsBackToTheSolverArrangement()
    {
        // Playing [5Bk, 5M, 8B, 8R] only works by dismantling BOTH runs (group the fives,
        // extend both 6-7 tails with the eights). No proper subset of sets recomposes, so
        // the stabilizer must hand back the solver's own result untouched.
        var blueRun = VSet(T(5, TileColor.Blue), T(6, TileColor.Blue), T(7, TileColor.Blue));
        var redRun = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var board = new Solution { IsValid = true, Runs = [blueRun, redRun] };
        var rack = new Set([T(5, TileColor.Black), T(5, TileColor.Mango),
            T(8, TileColor.Blue), T(8, TileColor.Red)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(4);

        var result = BoardStabilizer.Stabilize(board, solved);

        result.Should().BeSameAs(solved);
    }

    [Fact]
    public void BoardJokerInABrokenSet_IsReplacedFreely()
    {
        // [3B, joker(4B), 5B]: playing the real 4B and 6B rebuilds the run with the joker
        // pushed to an end — one broken set; the elevens group stays frozen.
        var jokerRun = VSet(T(3, TileColor.Blue), new Tile(4, TileColor.Blue, isJoker: true),
            T(5, TileColor.Blue));
        var elevens = VSet(T(11, TileColor.Blue), T(11, TileColor.Red), T(11, TileColor.Mango));
        var board = new Solution { IsValid = true, Runs = [jokerRun], Groups = [elevens] };
        var rack = new Set([T(4, TileColor.Blue), T(6, TileColor.Blue)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(2);

        var result = BoardStabilizer.Stabilize(board, solved);

        result.BestSolution.Groups.Should().ContainSingle().Which.Should().BeSameAs(elevens);
        var run = result.BestSolution.Runs.Should().ContainSingle().Subject;
        run.Tiles.Should().HaveCount(5);
        run.Tiles.Count(t => t.IsJoker).Should().Be(1);
        result.JokersToPlay.Should().Be(0, "the joker already lived on the board");
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void NotFoundOrNothingPlayed_PassesThrough()
    {
        var board = new Solution
        {
            IsValid = true,
            Runs = [VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };

        var invalid = SolverResult.Invalid("test");
        BoardStabilizer.Stabilize(board, invalid).Should().BeSameAs(invalid);

        var noOp = SolverResult.FromSolution("test", board, tilesToPlay: []);
        BoardStabilizer.Stabilize(board, noOp).Should().BeSameAs(noOp);
    }
}
