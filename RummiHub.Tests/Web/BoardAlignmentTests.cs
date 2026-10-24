using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Web.Rendering;

namespace RummiHub.Tests.Web;

public class BoardAlignmentTests
{
    private static ValidSet Set(params Tile[] tiles) => new(tiles);
    private static Tile T(int value, TileColor color) => new(value, color);

    [Fact]
    public void Align_ReordersNextRunsToMatchPreviousOrder()
    {
        var runA = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var runB = Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var previous = new Solution { Sets = [runA, runB] };

        // Solver returns the same runs in a different order (B first), A extended with a 4.
        var runBNext = Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var runANext = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue), T(4, TileColor.Blue));
        var next = new Solution { Sets = [runBNext, runANext] };

        var aligned = BoardAlignment.Align(previous, next);

        // A's match (now length 4) comes first, B's match second — following previous order.
        aligned.Sets.Should().Equal(runANext, runBNext);
    }

    [Fact]
    public void Align_AppendsBrandNewSetsLast()
    {
        var runA = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var previous = new Solution { Sets = [runA] };

        var newRun = Set(T(7, TileColor.Black), T(8, TileColor.Black), T(9, TileColor.Black));
        var runANext = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var next = new Solution { Sets = [newRun, runANext] };

        var aligned = BoardAlignment.Align(previous, next);

        aligned.Sets.Should().Equal(runANext, newRun);   // matched set kept in place, brand-new set last
    }

    [Fact]
    public void Align_PutsAGroupBuiltFromARunsTileBesideThatRun()
    {
        var mango = Set(T(1, TileColor.Mango), T(2, TileColor.Mango), T(3, TileColor.Mango));
        var red = Set(T(7, TileColor.Red), T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var blue = Set(T(11, TileColor.Blue), T(12, TileColor.Blue), T(13, TileColor.Blue));
        var previous = new Solution { Sets = [mango, red, blue] };

        var sevens = Set(T(7, TileColor.Red), T(7, TileColor.Blue), T(7, TileColor.Black));
        var shortRed = Set(T(8, TileColor.Red), T(9, TileColor.Red), T(10, TileColor.Red));
        var next = new Solution { Sets = [sevens, blue, mango, shortRed] };

        var aligned = BoardAlignment.Align(previous, next);

        aligned.Sets.Should().Equal(mango, shortRed, sevens, blue);
    }

    [Fact]
    public void Align_IsCosmetic_PreservesAllSetsAndTiles()
    {
        var previous = new Solution
        {
            Sets = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };
        var r1 = Set(T(9, TileColor.Red), T(10, TileColor.Red), T(11, TileColor.Red));
        var r2 = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var next = new Solution { Sets = [r1, r2] };

        var aligned = BoardAlignment.Align(previous, next);

        aligned.Sets.Should().BeEquivalentTo(next.Sets);   // same sets, order aside
    }
}
