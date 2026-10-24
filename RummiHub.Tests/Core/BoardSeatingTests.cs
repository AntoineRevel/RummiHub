using FluentAssertions;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class BoardSeatingTests
{
    private static Tile T(int value, TileColor color) => new(value, color);
    private static ValidSet Set(params Tile[] tiles) => new(tiles);

    [Fact]
    public void Seat_PutsASetBuiltFromAnotherSetsTilesRightBesideIt()
    {
        var first = Set(T(1, TileColor.Mango), T(2, TileColor.Mango), T(3, TileColor.Mango));
        var red = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var last = Set(T(11, TileColor.Blue), T(12, TileColor.Blue), T(13, TileColor.Blue));

        var shortRed = Set(T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var sevens = Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black));
        var fresh = Set(T(4, TileColor.Black), T(5, TileColor.Black), T(6, TileColor.Black));

        var seated = BoardSeating.Seat([first, red, last], [fresh, sevens, last, shortRed, first]);

        seated.Should().Equal(first, shortRed, sevens, last, fresh);
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
}
