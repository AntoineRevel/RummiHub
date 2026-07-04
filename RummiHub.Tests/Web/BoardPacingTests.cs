using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Web.Rendering;

namespace RummiHub.Tests.Web;

public class BoardPacingTests
{
    private static ValidSet Set(params Tile[] tiles) => new(tiles);
    private static Tile T(int value, TileColor color) => new(value, color);

    [Fact]
    public void EstimateMs_UnchangedBoard_ReturnsZero()
    {
        var previous = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))],
            Groups = [Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black))]
        };
        var next = new Solution
        {
            Runs = [Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue))],
            Groups = [Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black))]
        };

        BoardPacing.EstimateMs(previous, next).Should().Be(0);
    }

    [Fact]
    public void EstimateMs_TileAppendedToLastSet_IsOneStep()
    {
        var previous = new Solution
        {
            Runs =
            [
                Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue)),
                Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red))
            ]
        };
        var next = new Solution
        {
            Runs =
            [
                Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue)),
                Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red), T(8, TileColor.Red))
            ]
        };

        // The set kept its index, so only the entering tile is a step — no block step.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(1));
    }

    [Fact]
    public void EstimateMs_WholeSetRelocated_IsOneBlockStep()
    {
        var previous = new Solution
        {
            Runs =
            [
                Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue)),
                Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red))
            ]
        };
        // The first run is gone: the red run changes reading-order index but not composition.
        var next = new Solution
        {
            Runs = [Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red))]
        };

        // The whole run glides as one block — 1 step, not one per tile.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(1));
    }

    [Fact]
    public void EstimateMs_SetExtendedAndRelocated_IsBlockPlusEnter()
    {
        var previous = new Solution
        {
            Runs =
            [
                Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue)),
                Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red))
            ]
        };
        var next = new Solution
        {
            Runs = [Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red), T(8, TileColor.Red))]
        };

        // Kept tiles glide as one block, then the new tile arrives on top: 2 steps.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(2));
    }

    [Fact]
    public void EstimateMs_SetUnchangedSameIndex_IsZero()
    {
        var previous = new Solution
        {
            Runs = [Set(T(9, TileColor.Black), T(10, TileColor.Black), T(11, TileColor.Black))]
        };
        var next = new Solution
        {
            Runs = [Set(T(9, TileColor.Black), T(10, TileColor.Black), T(11, TileColor.Black))]
        };

        BoardPacing.EstimateMs(previous, next).Should().Be(0);
    }

    [Fact]
    public void EstimateMs_DuplicateTiles_MatchedByOccurrence()
    {
        // Two copies of 5-Red on the board: one in a run, one in a group. Only the run
        // relocates; the group's duplicate must not be dragged into the count.
        var previous = new Solution
        {
            Runs =
            [
                Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue)),
                Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red))
            ],
            Groups = [Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black))]
        };
        var next = new Solution
        {
            Runs = [Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red))],
            Groups = [Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black))]
        };

        // Only the relocated run's block counts — one step, not two.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(1));
    }

    [Fact]
    public void DurationForSteps_ZeroSteps_IsZero()
    {
        BoardPacing.DurationForSteps(0).Should().Be(0);
    }

    [Fact]
    public void DurationForSteps_GrowsLinearly()
    {
        // Strictly sequential, no duration cap: each extra step adds exactly one interval.
        for (var steps = 1; steps < 40; steps++)
        {
            (BoardPacing.DurationForSteps(steps + 1) - BoardPacing.DurationForSteps(steps))
                .Should().Be(BoardPacing.StartIntervalMs);
        }
    }
}
