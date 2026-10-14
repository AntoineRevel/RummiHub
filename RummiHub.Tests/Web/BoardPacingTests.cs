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
    public void EstimateMs_TileAppendedToLastSet_PlacesOneTile()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun] };
        var next = new Solution { Runs = [BlueRun, RedRunExtended] };

        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForPlacedTiles(1));
    }

    [Fact]
    public void EstimateMs_TileInsertedMidBoard_ReflowIsNotAMove()
    {
        // The Red run shifts to make room for the 4-Blue: that's act 2's single glide, not a
        // move of its own — only the placed tile counts.
        var blueRunExtended = Set(
            T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue), T(4, TileColor.Blue));
        var previous = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };
        var next = new Solution { Runs = [blueRunExtended, RedRun], Groups = [FivesGroup] };

        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForPlacedTiles(1));
    }

    [Fact]
    public void EstimateMs_NewSet_PlacesEachOfItsTiles()
    {
        var previous = new Solution { Runs = [BlueRun] };
        var next = new Solution { Runs = [BlueRun, RedRun] };

        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForPlacedTiles(3));
    }

    [Fact]
    public void EstimateMs_BoardTileChangingSet_IsPlacedToo()
    {
        // The 4-Blue leaves its run for a new group with the played 4-Red and 4-Black: it is
        // picked up and dropped like the played tiles — 3 placed tiles.
        var longBlueRun = Set(
            T(1, TileColor.Blue), T(2, TileColor.Blue), T(3, TileColor.Blue), T(4, TileColor.Blue));
        var foursGroup = Set(T(4, TileColor.Blue), T(4, TileColor.Red), T(4, TileColor.Black));
        var previous = new Solution { Runs = [longBlueRun] };
        var next = new Solution { Runs = [BlueRun], Groups = [foursGroup] };

        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForPlacedTiles(3));
    }

    [Fact]
    public void EstimateMs_DuplicateTiles_MatchedByOccurrence()
    {
        // A 5-Red already rests in FivesGroup; the other 5-Red is played into a new run. The
        // group keeps its own copy: the new run's 3 tiles are placed, nothing more.
        var previous = new Solution { Runs = [BlueRun], Groups = [FivesGroup] };
        var next = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };

        BoardPacing.EstimateMs(previous, next).Should().Be(BoardPacing.DurationForPlacedTiles(3));
    }

    [Fact]
    public void EnteredIndices_UnchangedBoard_IsEmpty()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };
        var next = new Solution { Runs = [BlueRun, RedRun], Groups = [FivesGroup] };

        BoardPacing.EnteredIndices(previous, next).Should().BeEmpty();
    }

    [Fact]
    public void EnteredIndices_TileAppended_ReturnsItsReadingIndex()
    {
        var previous = new Solution { Runs = [BlueRun, RedRun] };
        var next = new Solution { Runs = [BlueRun, RedRunExtended] };

        // Reading order: BlueRun tiles 0-2, RedRunExtended tiles 3-6 — the appended 8-Red is 6.
        BoardPacing.EnteredIndices(previous, next).Should().Equal(6);
    }

    [Fact]
    public void EnteredIndices_DuplicateAlreadyOnBoard_FlagsTheNewCopyOnly()
    {
        // A 5-Red already sits in FivesGroup; the player now plays the OTHER 5-Red into a new
        // run. The new copy (index 3, first tile of the new run) must be flagged — never the
        // twin resting in the group.
        var previous = new Solution { Runs = [BlueRun], Groups = [FivesGroup] };
        var newRedRun = Set(T(5, TileColor.Red), T(6, TileColor.Red), T(7, TileColor.Red));
        var next = new Solution { Runs = [BlueRun, newRedRun], Groups = [FivesGroup] };

        BoardPacing.EnteredIndices(previous, next).Should().Equal(3, 4, 5);
    }

    [Fact]
    public void EnteredIndices_TileInsertedIntoExistingSet_FlagsThatOccurrence()
    {
        // FivesGroup gains a 5-Mango: only that tile's reading index (after BlueRun's 0-2)
        // is flagged, by per-occurrence diff against the paired previous set.
        var extendedGroup = Set(
            T(5, TileColor.Red), T(5, TileColor.Blue), T(5, TileColor.Black), T(5, TileColor.Mango));
        var previous = new Solution { Runs = [BlueRun], Groups = [FivesGroup] };
        var next = new Solution { Runs = [BlueRun], Groups = [extendedGroup] };

        BoardPacing.EnteredIndices(previous, next).Should().Equal(6);
    }

    [Fact]
    public void DurationForPlacedTiles_NothingPlaced_IsZero()
    {
        BoardPacing.DurationForPlacedTiles(0).Should().Be(0);
    }

    [Fact]
    public void DurationForPlacedTiles_OneTile_CoversTheThreeActs()
    {
        // Take-off, the board making room, the drop.
        BoardPacing.DurationForPlacedTiles(1).Should().BeGreaterThanOrEqualTo(
            BoardPacing.FlightMs + BoardPacing.RoomMs + BoardPacing.DropMs);
    }

    [Fact]
    public void DurationForPlacedTiles_EachExtraTileAddsATakeOffAndADrop()
    {
        for (var placed = 1; placed < 40; placed++)
        {
            (BoardPacing.DurationForPlacedTiles(placed + 1) - BoardPacing.DurationForPlacedTiles(placed))
                .Should().Be(BoardPacing.StaggerMs + BoardPacing.DropStaggerMs);
        }
    }
}
