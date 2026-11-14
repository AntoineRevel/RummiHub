using AwesomeAssertions;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class BoardSeatingTests
{
    private static Tile T(int value, TileColor color) => new(value, color);
    private static ValidSet Set(params Tile[] tiles) => new(tiles);

    [Fact]
    public void Seat_PutsASetBuiltAroundATileTakenFromASetLast_SoNothingIsPushed()
    {
        var first = Set(T(1, TileColor.Mango), T(2, TileColor.Mango), T(3, TileColor.Mango));
        var red = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var last = Set(T(11, TileColor.Blue), T(12, TileColor.Blue), T(13, TileColor.Blue));

        var shortRed = Set(T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var sevens = Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black));
        var fresh = Set(T(4, TileColor.Black), T(5, TileColor.Black), T(6, TileColor.Black));

        var seated = BoardSeating.Seat([first, red, last], [fresh, sevens, last, shortRed, first]);

        seated.Should().Equal(first, shortRed, last, fresh, sevens);
    }

    [Fact]
    public void Seat_PutsASetPlayedFromTheHandLast_EvenWhenTheBoardHoldsTheSameTiles()
    {
        // Each tile exists twice: the sevens played from the hand look like the 7R of the
        // red run and the 7B of the blue one, but take nothing from them.
        var red = Set(T(6, TileColor.Red), T(7, TileColor.Red), T(8, TileColor.Red));
        var blue = Set(T(7, TileColor.Blue), T(8, TileColor.Blue), T(9, TileColor.Blue));
        var last = Set(T(11, TileColor.Black), T(12, TileColor.Black), T(13, TileColor.Black));

        var sevens = Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Mango));

        BoardSeating.Seat([red, blue, last], [sevens, red, blue, last]).Should().Equal(red, blue, last, sevens);
    }

    [Fact]
    public void Seat_SeatsEachRebuiltSetBesideTheSetThatGaveItsTiles_NotBesideALookalike()
    {
        // The 7R of the new sevens leaves the long run; the short run that also holds a 7R
        // keeps all its tiles and gives nothing.
        var shortRun = Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var middle = Set(T(1, TileColor.Black), T(2, TileColor.Black), T(3, TileColor.Black));
        var longRun = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));

        var cut = Set(T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var sevens = Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black));

        BoardSeating.Seat([shortRun, middle, longRun], [sevens, shortRun, middle, cut])
            .Should().Equal(shortRun, middle, cut, sevens);

        // Had the lookalike been taken for the origin, the halves of this cut would be apart.
        var low = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red));
        var high = Set(T(9, TileColor.Red), T(10, TileColor.Red), T(11, TileColor.Red));
        var longer = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red), T(11, TileColor.Red));
        BoardSeating.Seat([shortRun, longer, middle], [high, shortRun, middle, low])
            .Should().Equal(shortRun, low, high, middle);
    }

    [Fact]
    public void MovedTiles_CountsNewTilesAndTilesChangingSet()
    {
        var red = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var shortRed = Set(T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var sevens = Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black));

        // 7B and 7Bk come from the hand, 7R leaves the run.
        BoardSeating.MovedTiles([red], [shortRed, sevens]).Should().Be(3);
    }

    [Fact]
    public void Pair_GoesToTheLargestOverlapFirst_WhateverTheBoardOrder()
    {
        // The long run is the old blue run plus 7B and 8B. The sevens come first on the board
        // and share 7B with it, but must not take it away from the run it grew out of.
        var sevens = Set(T(7, TileColor.Blue), T(7, TileColor.Red), T(7, TileColor.Black), T(7, TileColor.Mango));
        var blue = Set(T(3, TileColor.Blue), T(4, TileColor.Blue), T(5, TileColor.Blue), T(6, TileColor.Blue));

        var longBlue = Set(T(3, TileColor.Blue), T(4, TileColor.Blue), T(5, TileColor.Blue), T(6, TileColor.Blue),
            T(7, TileColor.Blue), T(8, TileColor.Blue));
        var threeSevens = Set(T(7, TileColor.Red), T(7, TileColor.Black), T(7, TileColor.Mango));

        BoardSeating.Pair([sevens, blue], [longBlue, threeSevens]).Should().Equal((1, 4), (0, 3));
        BoardSeating.MovedTiles([sevens, blue], [longBlue, threeSevens]).Should().Be(2);
    }

    [Fact]
    public void Pair_KeepsASetWholeRatherThanGiveItsSeatToOneThatSharesAsMuch()
    {
        var three = Set(T(13, TileColor.Blue), T(13, TileColor.Red), T(13, TileColor.Mango));
        var four = Set(T(13, TileColor.Red), T(13, TileColor.Mango), T(13, TileColor.Black), T(13, TileColor.Blue));

        BoardSeating.Seat([three, four], [four, three]).Should().Equal(three, four);
    }

    [Fact]
    public void Seat_KeepsTheHalvesOfACutRunLowEndFirst()
    {
        var run = Set(T(6, TileColor.Black), T(7, TileColor.Black), T(8, TileColor.Black),
            T(9, TileColor.Black), T(10, TileColor.Black));
        var ones = Set(T(1, TileColor.Blue), T(1, TileColor.Red), T(1, TileColor.Mango));

        var low = Set(T(6, TileColor.Black), T(7, TileColor.Black), T(8, TileColor.Black));
        var high = Set(T(8, TileColor.Black), T(9, TileColor.Black), T(10, TileColor.Black));

        BoardSeating.Seat([run, ones], [high, ones, low]).Should().Equal(low, high, ones);
    }

    [Fact]
    public void Seat_PutsTheSetBuiltWithAJokerTakenFromAGroupLast_AndLeavesTheGroupInPlace()
    {
        // 13 13 joker: a 13 from the hand takes the joker's place, the joker leaves for 1 2 3.
        var thirteens = Set(T(13, TileColor.Blue), T(13, TileColor.Red), new Tile(13, TileColor.Black, isJoker: true));
        var after = Set(T(4, TileColor.Mango), T(5, TileColor.Mango), T(6, TileColor.Mango));

        var full = Set(T(13, TileColor.Blue), T(13, TileColor.Red), T(13, TileColor.Black));
        var low = Set(T(1, TileColor.Red), new Tile(2, TileColor.Red, isJoker: true), T(3, TileColor.Red));

        BoardSeating.Seat([thirteens, after], [low, full, after]).Should().Equal(full, after, low);
    }
}
