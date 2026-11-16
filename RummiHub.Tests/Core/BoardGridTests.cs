using AwesomeAssertions;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class BoardGridTests
{
    private const int Columns = 40;

    private static Tile T(int value, TileColor color) => new(value, color);
    private static ValidSet Set(params Tile[] tiles) => new(tiles);

    private static ValidSet Run(TileColor color, int from, int to) =>
        new(Enumerable.Range(from, to - from + 1).Select(value => T(value, color)).ToArray());

    private static BoardSpot[] Fresh(params ValidSet[] sets) => BoardGrid.Place([], [], sets, Columns);

    [Fact]
    public void Place_LaysAFreshBoardInReadingOrder_WithRoomBetweenSets()
    {
        var spots = BoardGrid.Place([], [], [Run(TileColor.Red, 1, 3), Run(TileColor.Blue, 1, 4), Run(TileColor.Black, 1, 3)], 20);

        // 6 columns, 3 of room, 8 columns: the third set (6 more) no longer fits the row.
        spots.Should().Equal(new BoardSpot(0, 0), new BoardSpot(0, 9), new BoardSpot(1, 0));
    }

    [Fact]
    public void Place_LeavesUntouchedSetsWhereTheyAre_WhateverTheirOrder()
    {
        var a = Run(TileColor.Red, 1, 3);
        var b = Run(TileColor.Blue, 1, 3);
        var c = Run(TileColor.Black, 1, 3);
        var spots = Fresh(a, b, c);

        BoardGrid.Place([a, b, c], spots, [c, a, b], Columns).Should().Equal(spots[2], spots[0], spots[1]);
    }

    [Fact]
    public void Place_GrowsASetInPlace_WhenTheRoomIsFree()
    {
        var a = Run(TileColor.Red, 4, 6);
        var b = Run(TileColor.Blue, 1, 3);
        var spots = Fresh(a, b);

        var grown = BoardGrid.Place([a, b], spots, [Run(TileColor.Red, 4, 7), b], Columns);

        grown.Should().Equal(spots);
    }

    [Fact]
    public void Place_ExtendsARunToTheLeft_WhenATileIsAddedAtItsLowEnd()
    {
        var a = Run(TileColor.Red, 1, 3);
        var b = Run(TileColor.Blue, 5, 7);
        var spots = Fresh(a, b); // b at column 9

        var grown = BoardGrid.Place([a, b], spots, [a, Run(TileColor.Blue, 4, 7)], Columns);

        // The 5, 6 and 7 stay on their cells: the run starts one tile further left.
        grown.Should().Equal(spots[0], new BoardSpot(0, spots[1].Col - BoardGrid.TileWidth));
    }

    [Fact]
    public void Place_MovesOnlyTheSetThatNoLongerFits_ToTheNearestFreeRoomBelow()
    {
        var a = Run(TileColor.Red, 1, 3);
        var b = Run(TileColor.Blue, 1, 3);
        var c = Run(TileColor.Black, 1, 3);
        // A full row, sets a half tile apart, and a second row under it.
        BoardSpot[] spots = [new(0, 0), new(0, 7), new(0, 14), new(1, 0)];
        var d = Run(TileColor.Mango, 1, 3);

        var next = BoardGrid.Place([a, b, c, d], spots, [a, Run(TileColor.Blue, 1, 4), c, d], 20);

        next[0].Should().Be(spots[0]);
        next[2].Should().Be(spots[2]);
        next[3].Should().Be(spots[3]);
        next[1].Should().Be(new BoardSpot(1, 7), "the grown set goes under its old spot, nothing else moves");
    }

    [Fact]
    public void Place_KeepsBothHalvesOfACutRunInItsRoom()
    {
        var run = Run(TileColor.Red, 1, 7);
        var other = Run(TileColor.Blue, 1, 3);
        var spots = Fresh(run, other);

        // The 4 leaves for a group; 1-3 and 5-7 stay.
        var low = Run(TileColor.Red, 1, 3);
        var high = Run(TileColor.Red, 5, 7);
        var fours = Set(T(4, TileColor.Red), T(4, TileColor.Blue), T(4, TileColor.Black));
        var next = BoardGrid.Place([run, other], spots, [low, high, other, fours], Columns);

        next[0].Should().Be(spots[0]);
        next[1].Should().Be(new BoardSpot(0, 4 * BoardGrid.TileWidth), "5, 6 and 7 stay on their cells");
        next[2].Should().Be(spots[1]);
    }

    [Fact]
    public void Place_NeverLandsOnCellsHeldByASetItTakesNothingFrom()
    {
        // The blue run is taken apart by this move; the set played from the hand must not
        // land on its cells, where its tiles still lie until their own step.
        var a = Run(TileColor.Red, 1, 3);
        var blue = Run(TileColor.Blue, 1, 3);
        BoardSpot[] spots = [new(0, 0), new(0, 9)];

        var ones = Set(T(1, TileColor.Blue), T(1, TileColor.Black), T(1, TileColor.Mango));
        var twos = Set(T(2, TileColor.Blue), T(2, TileColor.Black), T(2, TileColor.Mango));
        var threes = Set(T(3, TileColor.Blue), T(3, TileColor.Black), T(3, TileColor.Mango));
        var fresh = Run(TileColor.Black, 10, 12);
        var next = BoardGrid.Place([a, blue], spots, [a, ones, twos, threes, fresh], 20);

        var cells = Enumerable.Range(9, 6).ToHashSet();
        next[4].Row.Should().NotBe(0);
        (next[4].Row == 0 && Enumerable.Range(next[4].Col, 6).Any(cells.Contains)).Should().BeFalse();
    }

    [Fact]
    public void Place_ReusesARoomLeftEmpty_ForASetPlayedLater()
    {
        var a = Run(TileColor.Red, 1, 3);
        var b = Run(TileColor.Blue, 1, 3);
        var c = Run(TileColor.Black, 1, 3);
        BoardSpot[] spots = [new(0, 0), new(1, 0), new(2, 0)];

        // b is gone since an earlier move; a new set takes the first free room.
        var fresh = Run(TileColor.Mango, 1, 3);
        var next = BoardGrid.Place([a, c], [spots[0], spots[2]], [a, c, fresh], 6);

        next.Should().Equal(spots[0], spots[2], spots[1]);
    }

    [Fact]
    public void Place_LaysASetLongerThanTheTableAtTheStartOfARow()
    {
        var a = Run(TileColor.Red, 1, 3);
        var longRun = Run(TileColor.Blue, 1, 13);

        var spots = BoardGrid.Place([], [], [a, longRun], 16);

        spots.Should().Equal(new BoardSpot(0, 0), new BoardSpot(1, 0));
    }
}
