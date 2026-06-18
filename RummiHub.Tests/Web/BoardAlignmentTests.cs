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
        var previous = new Solution { Runs = [runA, runB] };

        // Solver returns the same runs in a different order (B first), A extended with a 4.
        var runBNext = Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var runANext = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue), T(4, TileColor.Blue));
        var next = new Solution { Runs = [runBNext, runANext] };

        var aligned = BoardAlignment.Align(previous, next);

        // A's match (now length 4) comes first, B's match second — following previous order.
        aligned.Runs[0].Tiles.Should().HaveCount(4);
        aligned.Runs[0].Tiles[0].Value.Should().Be(1);
        aligned.Runs[1].Tiles[0].Value.Should().Be(5);
    }

    [Fact]
    public void Align_AppendsBrandNewSetsLast()
    {
        var runA = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var previous = new Solution { Runs = [runA] };

        var newRun = Set(T(7, TileColor.Black), T(8, TileColor.Black), T(9, TileColor.Black));
        var runANext = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var next = new Solution { Runs = [newRun, runANext] };

        var aligned = BoardAlignment.Align(previous, next);

        aligned.Runs[0].Tiles[0].Value.Should().Be(1);   // matched set kept in place
        aligned.Runs[1].Tiles[0].Value.Should().Be(7);   // brand-new set appended last
    }

    [Fact]
    public void Align_AlignsRunsAndGroupsIndependently()
    {
        var run = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var group = Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black));
        var previous = new Solution { Runs = [run], Groups = [group] };

        var next = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))],
            Groups = [Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black), T(5, TileColor.Mango))]
        };

        var aligned = BoardAlignment.Align(previous, next);

        aligned.Runs.Should().HaveCount(1);
        aligned.Groups.Should().HaveCount(1);
        aligned.Groups[0].Tiles.Should().HaveCount(4);
    }

    [Fact]
    public void Align_IsCosmetic_PreservesAllSetsAndTiles()
    {
        var previous = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))]
        };
        var r1 = Set(T(9, TileColor.Red), T(10, TileColor.Red), T(11, TileColor.Red));
        var r2 = Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
        var next = new Solution { Runs = [r1, r2] };

        var aligned = BoardAlignment.Align(previous, next);

        aligned.Runs.Should().BeEquivalentTo(next.Runs);   // same sets, order aside
    }
}
