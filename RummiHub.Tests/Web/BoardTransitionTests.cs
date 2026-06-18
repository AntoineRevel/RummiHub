using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Web.Rendering;

namespace RummiHub.Tests.Web;

public class BoardTransitionTests
{
    private static ValidSet Set(params Tile[] tiles) => new(tiles);
    private static Tile T(int value, TileColor color) => new(value, color);

    [Fact]
    public void Compute_IdenticalBoard_IsZero()
    {
        var board = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };
        var same = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };

        BoardTransition.Compute(board, same).Should().Be((0, 0));
    }

    [Fact]
    public void Compute_ExtendRun_OneEnteredNoneMoved()
    {
        var previous = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };
        var next = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue), T(4, TileColor.Blue))]
        };

        // 1,2,3 keep reading-order indices 0,1,2; the 4 is a new key at index 3.
        BoardTransition.Compute(previous, next).Should().Be((0, 1));
    }

    [Fact]
    public void Compute_ExtendGroup_OneEnteredNoneMoved()
    {
        var previous = new Solution
        {
            Groups = [Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black))]
        };
        var next = new Solution
        {
            Groups = [Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black), T(7, TileColor.Mango))]
        };

        BoardTransition.Compute(previous, next).Should().Be((0, 1));
    }

    [Fact]
    public void Compute_GrowingEarlierSetShiftsLaterTiles_CountedAsMoved()
    {
        // Adding a tile to the run shifts every group tile's global reading-order index, so the
        // server counts them as moved. This is intentional: an upper-bound estimate keeps the AI
        // pause a safe superset of the (usually smaller) client animation.
        var previous = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))],
            Groups = [Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black))]
        };
        var next = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue), T(4, TileColor.Blue))],
            Groups = [Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black))]
        };

        BoardTransition.Compute(previous, next).Should().Be((3, 1));
    }
}
