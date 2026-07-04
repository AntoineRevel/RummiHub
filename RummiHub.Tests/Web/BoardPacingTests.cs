using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Web.Rendering;

namespace RummiHub.Tests.Web;

public class BoardPacingTests
{
    private static ValidSet Set(params Tile[] tiles) => new(tiles);
    private static Tile T(int value, TileColor color) => new(value, color);

    // EstimateMs never mutates its inputs, so fixtures are safely shared across tests.
    // FivesGroup and RedRun both hold a 5-Red: the board-wide duplicate pair.
    private static readonly ValidSet BlueRun =
        Set(T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue));
    private static readonly ValidSet RedRun =
        Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
    private static readonly ValidSet RedRunExtended =
        Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red), T(8, TileColor.Red));
    private static readonly ValidSet FivesGroup =
        Set(T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black));

    [Fact]
    public void EstimateMs_UnchangedBoard_ReturnsZero()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };
        var next = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };

        BoardPacing.EstimateMs(previous, next).Should().Be(0);
    }

    [Fact]
    public void EstimateMs_TileAppendedToLastSet_IsOneStep()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun] };
        var next = new Solution { Runs = [BlueRun, RedRunExtended] };

        // The set kept its index, so only the entering tile is a step — no block step.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(1));
    }

    [Fact]
    public void EstimateMs_WholeSetRelocated_IsOneBlockStep()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun] };
        var next = new Solution { Runs = [RedRun] };

        // Same composition at a new reading-order index: ONE block step, not one per tile.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(1));
    }

    [Fact]
    public void EstimateMs_SetExtendedAndRelocated_IsBlockPlusEnter()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun] };
        var next = new Solution { Runs = [RedRunExtended] };

        // Kept tiles glide as one block, then the new tile arrives on top: 2 steps.
        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForSteps(2));
    }

    [Fact]
    public void EstimateMs_SetUnchangedSameIndex_IsZero()
    {
        var previous = new Solution { Runs = [RedRun] };
        var next = new Solution { Runs = [RedRun] };

        BoardPacing.EstimateMs(previous, next).Should().Be(0);
    }

    [Fact]
    public void EstimateMs_DuplicateTiles_MatchedByOccurrence()
    {
        // Two 5-Red on the board (run + group); only the run relocates. The group's
        // duplicate must not be dragged into the count: one step, not two.
        var previous = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };
        var next = new Solution { Runs = [RedRun], Groups = [FivesGroup] };

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
        // Strictly sequential and uncapped: every extra step adds exactly one interval.
        for (var steps = 1; steps < 40; steps++)
        {
            (BoardPacing.DurationForSteps(steps + 1) - BoardPacing.DurationForSteps(steps))
                .Should().Be(BoardPacing.StartIntervalMs);
        }
    }
}
