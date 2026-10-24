using System.Text.Json;
using FluentAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Solver.Graph;

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
        var board = new Solution { IsValid = true, Sets = [blueRun, redRun] };
        var rack = new Set([T(11, TileColor.Blue), T(11, TileColor.Red), T(11, TileColor.Black)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(3);

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Should().BeSameAs(blueRun);
        sets[1].Should().BeSameAs(redRun);
        sets[2].Tiles.Should().AllSatisfy(t => t.Value.Should().Be(11));
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void ExtendingARun_OnlyBreaksThatRun()
    {
        var blueRun = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var redRun = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var nines = VSet(T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Mango));
        var board = new Solution { IsValid = true, Sets = [blueRun, redRun, nines] };
        var rack = new Set([T(4, TileColor.Blue)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().ContainSingle();

        var result = BoardStabilizer.Stabilize(board, solved);

        // The blue run simply grew by the played tile, in its seat…
        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Tiles.Select(t => (t.Color, t.Value)).Should().Equal(
            (TileColor.Blue, 1), (TileColor.Blue, 2), (TileColor.Blue, 3), (TileColor.Blue, 4));
        // …and the untouched sets come back as the very same instances.
        sets[1].Should().BeSameAs(redRun);
        sets[2].Should().BeSameAs(nines);
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
        var board = new Solution { IsValid = true, Sets = [blackRun, ones] };
        var rack = new Set([T(8, TileColor.Black)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().ContainSingle();

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Tiles.Should().HaveCount(3);
        sets[1].Tiles.Should().HaveCount(3);
        sets[2].Should().BeSameAs(ones);
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
        var board = new Solution { IsValid = true, Sets = [lowBlue, highBlue, nines] };

        var churned = new Solution { IsValid = true };
        churned.Sets.Add(VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue),
            T(4, TileColor.Blue), T(5, TileColor.Blue), T(6, TileColor.Blue)));
        churned.Sets.Add(VSet(T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Mango),
            T(9, TileColor.Black)));
        var solved = SolverResult.FromSolution(
            "test", churned, [T(9, TileColor.Black)], jokersToPlay: 0, score: 57);

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Should().BeSameAs(lowBlue);
        sets[1].Should().BeSameAs(highBlue);
        sets[2].Tiles.Should().HaveCount(4);
        result.TilesToPlay.Should().BeEquivalentTo(new[] { T(9, TileColor.Black) });
        ShouldBeLegalOver(result, board, new Set([T(9, TileColor.Black)]));
    }

    [Fact]
    public void MoveNeedingTheWholeTable_KeepsTheSolverSetsSeatedOnTheOldOnes()
    {
        // Playing [5Bk, 5M, 8B, 8R] only works by dismantling BOTH runs (group the fives,
        // extend both 6-7 tails with the eights). No proper subset of sets recomposes, so
        // the solver's own sets stand, each run in its old seat and the fives beside the
        // first run they came from.
        var blueRun = VSet(T(5, TileColor.Blue), T(6, TileColor.Blue), T(7, TileColor.Blue));
        var redRun = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var board = new Solution { IsValid = true, Sets = [blueRun, redRun] };
        var rack = new Set([T(5, TileColor.Black), T(5, TileColor.Mango),
            T(8, TileColor.Blue), T(8, TileColor.Red)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(4);

        var result = BoardStabilizer.Stabilize(board, solved);

        result.TilesToPlay.Should().BeEquivalentTo(solved.TilesToPlay);
        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Tiles.Should().AllSatisfy(t => t.Color.Should().Be(TileColor.Blue));
        sets[1].IsGroup.Should().BeTrue();
        sets[2].Tiles.Should().AllSatisfy(t => t.Color.Should().Be(TileColor.Red));
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void RunTurnedIntoAGroup_SitsBesideTheRunItCameFrom()
    {
        // 7B and 7Bk only fit by taking 7R off the red run: the new group is made of that
        // run's tile, so it lands right after the run, not after every other set.
        var mango = VSet(T(1, TileColor.Mango), T(2, TileColor.Mango), T(3, TileColor.Mango));
        var red = VSet(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var blue = VSet(T(11, TileColor.Blue), T(12, TileColor.Blue), T(13, TileColor.Blue));
        var board = new Solution { IsValid = true, Sets = [mango, red, blue] };
        var rack = new Set([T(7, TileColor.Blue), T(7, TileColor.Black)]);

        var result = BoardStabilizer.Stabilize(board, SolveFor(board, rack));

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(4);
        sets[0].Should().BeSameAs(mango);
        sets[1].Tiles.Select(t => t.Value).Should().Equal(8, 9, 10);
        sets[2].Tiles.Should().AllSatisfy(t => t.Value.Should().Be(7)).And.HaveCount(3);
        sets[3].Should().BeSameAs(blue);
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void BoardJokerInABrokenSet_IsReplacedFreely()
    {
        // [3B, joker(4B), 5B]: playing the real 4B and 6B rebuilds the run with the joker
        // pushed to an end — one broken set; the elevens group stays frozen.
        var jokerRun = VSet(T(3, TileColor.Blue), new Tile(4, TileColor.Blue, isJoker: true),
            T(5, TileColor.Blue));
        var elevens = VSet(T(11, TileColor.Blue), T(11, TileColor.Red), T(11, TileColor.Mango));
        var board = new Solution { IsValid = true, Sets = [jokerRun, elevens] };
        var rack = new Set([T(4, TileColor.Blue), T(6, TileColor.Blue)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(2);

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(2);
        sets[1].Should().BeSameAs(elevens);
        var run = sets[0];
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
            Sets = [VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };

        var invalid = SolverResult.Invalid("test");
        BoardStabilizer.Stabilize(board, invalid).Should().BeSameAs(invalid);

        var noOp = SolverResult.FromSolution("test", board, tilesToPlay: []);
        BoardStabilizer.Stabilize(board, noOp).Should().BeSameAs(noOp);
    }

    [Fact]
    public void Adding13ToAGroupOfThree_KeepsEveryOtherSetInPlace()
    {
        // The played 13 only completes the group. A from-scratch solver still hands back
        // every set rebuilt and reordered; the stabilizer must put them back and append
        // the new 13 without shuffling the three that were already there.
        var thirteens = VSet(T(13, TileColor.Black), T(13, TileColor.Blue), T(13, TileColor.Red));
        var blue = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var red = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var eights = VSet(T(8, TileColor.Mango), T(8, TileColor.Red), T(8, TileColor.Black));
        var board = new Solution { IsValid = true, Sets = [blue, red, thirteens, eights] };

        // Pointlessly reordered, and the group rebuilt in color order instead of
        // keeping Black, Blue, Red and appending Mango.
        var churned = new Solution { IsValid = true };
        churned.Sets.Add(VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red)));
        churned.Sets.Add(VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue)));
        churned.Sets.Add(VSet(T(8, TileColor.Mango), T(8, TileColor.Red), T(8, TileColor.Black)));
        churned.Sets.Add(VSet(
            T(13, TileColor.Blue), T(13, TileColor.Red), T(13, TileColor.Black), T(13, TileColor.Mango)));
        var solved = SolverResult.FromSolution("churn", churned, [T(13, TileColor.Mango)]);

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(4);
        sets[0].Should().BeSameAs(blue);
        sets[1].Should().BeSameAs(red);
        sets[3].Should().BeSameAs(eights);
        sets[2].Tiles.Select(t => (t.Color, t.Value))
            .Should().Equal(
                (TileColor.Black, 13),
                (TileColor.Blue, 13),
                (TileColor.Red, 13),
                (TileColor.Mango, 13));
        result.TilesToPlay.Should().BeEquivalentTo([T(13, TileColor.Mango)]);
        ShouldBeLegalOver(result, board, new Set([T(13, TileColor.Mango)]));
    }

    [Fact]
    public void ExtendingARunInTheMiddle_AppendsAndLeavesNeighbors()
    {
        var red = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var blue = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var mango = VSet(T(10, TileColor.Mango), T(11, TileColor.Mango), T(12, TileColor.Mango));
        var board = new Solution { IsValid = true, Sets = [red, blue, mango] };
        var rack = new Set([T(4, TileColor.Blue)]);

        var result = BoardStabilizer.Stabilize(board, SolveFor(board, rack));

        result.BestSolution.Sets[0].Should().BeSameAs(red);
        result.BestSolution.Sets[2].Should().BeSameAs(mango);
        result.BestSolution.Sets[1].Tiles.Select(t => t.Value).Should().Equal(1, 2, 3, 4);
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void ExtendingARunAtTheLowEnd_PrependsTheTile()
    {
        var run = VSet(T(5, TileColor.Blue), T(6, TileColor.Blue), T(7, TileColor.Blue));
        var other = VSet(T(1, TileColor.Red), T(1, TileColor.Black), T(1, TileColor.Mango));
        var board = new Solution { IsValid = true, Sets = [run, other] };
        var rack = new Set([T(4, TileColor.Blue)]);

        var result = BoardStabilizer.Stabilize(board, SolveFor(board, rack));

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(2);
        sets[0].Tiles.Select(t => t.Value).Should().Equal(4, 5, 6, 7);
        sets[1].Should().BeSameAs(other);
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void IndependentExtensions_TouchOnlyThoseSets()
    {
        var blue = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var red = VSet(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var keep = VSet(T(2, TileColor.Mango), T(3, TileColor.Mango), T(4, TileColor.Mango));
        var nines = VSet(T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Black));
        var board = new Solution { IsValid = true, Sets = [blue, red, keep, nines] };
        var rack = new Set([T(4, TileColor.Blue), T(8, TileColor.Red), T(9, TileColor.Mango)]);

        var result = BoardStabilizer.Stabilize(board, SolveFor(board, rack));

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(4);
        sets[0].Tiles.Select(t => t.Value).Should().Equal(1, 2, 3, 4);
        sets[1].Tiles.Select(t => t.Value).Should().Equal(5, 6, 7, 8);
        sets[2].Should().BeSameAs(keep);
        sets[3].Tiles.Select(t => t.Color)
            .Should().Equal(TileColor.Blue, TileColor.Red, TileColor.Black, TileColor.Mango);
        result.TilesToPlay.Should().BeEquivalentTo(rack.Tiles);
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void Manipulation_LeavesSetsTheMoveDoesNotNeed()
    {
        // 9-Mango and 10-Black cannot simply extend anything; placing them opens the joker
        // and several neighbouring sets. The 3-4-5 black run, the twos and the fours are
        // not part of that and must survive as the same instances.
        var blackRun = VSet(T(3, TileColor.Black), T(4, TileColor.Black), T(5, TileColor.Black));
        var blueRun = VSet(T(7, TileColor.Blue), T(8, TileColor.Blue), T(9, TileColor.Blue), T(10, TileColor.Blue));
        var redRun = VSet(T(10, TileColor.Red), T(11, TileColor.Red), T(12, TileColor.Red));
        var elevensA = VSet(T(11, TileColor.Blue), T(11, TileColor.Mango), T(11, TileColor.Black));
        var twos = VSet(T(2, TileColor.Blue), T(2, TileColor.Red), T(2, TileColor.Black));
        var fours = VSet(T(4, TileColor.Blue), T(4, TileColor.Red), T(4, TileColor.Mango), T(4, TileColor.Black));
        var elevensB = VSet(T(11, TileColor.Blue), T(11, TileColor.Red), T(11, TileColor.Mango));
        var twelves = VSet(T(12, TileColor.Blue), T(12, TileColor.Mango), T(12, TileColor.Black));
        var thirteens = VSet(new Tile(13, TileColor.Blue, isJoker: true), T(13, TileColor.Red), T(13, TileColor.Black));
        var board = new Solution
        {
            IsValid = true,
            Sets = [blackRun, blueRun, redRun, elevensA, twos, fours, elevensB, twelves, thirteens]
        };
        var rack = new Set([T(9, TileColor.Mango), T(10, TileColor.Black)]);

        var result = BoardStabilizer.Stabilize(board, SolveFor(board, rack));

        result.TilesToPlay.Should().BeEquivalentTo(rack.Tiles);
        result.BestSolution.Sets.Should().Contain(s => ReferenceEquals(s, blackRun))
            .And.Contain(s => ReferenceEquals(s, twos))
            .And.Contain(s => ReferenceEquals(s, fours));
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void GraphSolverArrangement_StillExtendsTheGroupInPlace()
    {
        var thirteens = VSet(T(13, TileColor.Blue), T(13, TileColor.Red), T(13, TileColor.Black));
        var blue = VSet(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var red = VSet(T(4, TileColor.Red), T(5, TileColor.Red), T(6, TileColor.Red));
        var board = new Solution { IsValid = true, Sets = [blue, red, thirteens] };
        var rack = new Set([T(13, TileColor.Mango)]);

        var solved = GraphSolver.Create(board.ToSet(), rack).SearchSolution();
        solved.Found.Should().BeTrue();
        solved.TilesToPlay.Should().Contain(T(13, TileColor.Mango));

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Should().BeSameAs(blue);
        sets[1].Should().BeSameAs(red);
        sets[2].Tiles.Should().HaveCount(4);
        sets[2].Tiles.Take(3).Select(t => t.Color)
            .Should().Equal(TileColor.Blue, TileColor.Red, TileColor.Black);
        ShouldBeLegalOver(result, board, rack);
    }

    [Fact]
    public void BoardReceivedOverJson_WithAJoker_StillStaysInPlace()
    {
        // Production game bb044f42, turn 13: 9M and 10M only extend the mango run. The
        // worker gets the board as JSON; if the joker lost the tile it stands for there, every
        // arrangement reusing the board's sets failed validation and the solver's sorted table
        // came back instead: the second ones-group, laid out of colour order, got reshuffled.
        var mango = VSet(T(6, TileColor.Mango), T(7, TileColor.Mango), T(8, TileColor.Mango));
        var ones = VSet(T(1, TileColor.Blue), T(1, TileColor.Mango), T(1, TileColor.Black), T(1, TileColor.Red));
        var thirteens = VSet(T(13, TileColor.Blue), new Tile(13, TileColor.Red, isJoker: true), T(13, TileColor.Black));
        var sent = new Solution { IsValid = true, Sets = [mango, ones, thirteens] };
        var board = JsonSerializer.Deserialize<Solution>(
            JsonSerializer.Serialize(sent, RummiJsonOptions.Default), RummiJsonOptions.Default)!;
        var rack = new Set([T(10, TileColor.Mango), T(9, TileColor.Mango)]);

        var solved = SolveFor(board, rack);
        solved.TilesToPlay.Should().HaveCount(2);

        var result = BoardStabilizer.Stabilize(board, solved);

        var sets = result.BestSolution.Sets;
        sets.Should().HaveCount(3);
        sets[0].Tiles.Select(t => t.Value).Should().Equal(6, 7, 8, 9, 10);
        sets[1].Should().BeSameAs(board.Sets[1]);
        sets[2].Should().BeSameAs(board.Sets[2]);
        ShouldBeLegalOver(result, board, rack);
    }
}
