using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Web.Rendering;

namespace RummiHub.Tests.Web;

public class BoardFlipKeysTests
{
    private static ValidSet Run(params Tile[] tiles) => new(tiles);

    [Fact]
    public void Assign_WalksRunsThenGroups_InReadingOrder()
    {
        var board = new Solution
        {
            Runs = [Run(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue))],
            Groups = [Run(new Tile(5, TileColor.Red), new Tile(5, TileColor.Black))]
        };

        BoardFlipKeys.Assign(board).Should().Equal(
            "1-Blue#0", "2-Blue#0", "5-Red#0", "5-Black#0");
    }

    [Fact]
    public void Assign_DuplicateTileType_GetsIncrementingOccurrenceIndex()
    {
        var board = new Solution
        {
            Runs = [Run(new Tile(7, TileColor.Red))],
            Groups = [Run(new Tile(7, TileColor.Red), new Tile(7, TileColor.Blue))]
        };

        // Two physical Red-7 across the board → #0 then #1; the Blue-7 is its own bucket.
        BoardFlipKeys.Assign(board).Should().Equal("7-Red#0", "7-Red#1", "7-Blue#0");
    }

    [Fact]
    public void Assign_AllJokers_FoldIntoSingleBucket()
    {
        var board = new Solution
        {
            Runs = [Run(new Tile(isJoker: true), new Tile(3, TileColor.Black), new Tile(isJoker: true))],
            Groups = []
        };

        BoardFlipKeys.Assign(board).Should().Equal("J#0", "3-Black#0", "J#1");
    }

    [Fact]
    public void Assign_UnmovedTile_KeepsSameFlipIdAcrossBoards()
    {
        var before = new Solution
        {
            Runs = [Run(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue))],
            Groups = []
        };
        var after = new Solution
        {
            Runs = [Run(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue))],
            // A new group was added elsewhere, but the run is untouched.
            Groups = [Run(new Tile(9, TileColor.Red), new Tile(9, TileColor.Blue), new Tile(9, TileColor.Black))]
        };

        var beforeKeys = BoardFlipKeys.Assign(before);
        var afterKeys = BoardFlipKeys.Assign(after);

        // The Blue 1-2-3 run keeps identical keys → those tiles are paired and animate in place.
        afterKeys.Take(3).Should().Equal(beforeKeys);
    }

    [Fact]
    public void Assign_TileMovedBetweenSets_KeepsItsTypeKey()
    {
        var before = new Solution
        {
            Runs = [Run(new Tile(4, TileColor.Red), new Tile(5, TileColor.Red), new Tile(6, TileColor.Red))],
            Groups = [Run(new Tile(8, TileColor.Red), new Tile(8, TileColor.Blue), new Tile(8, TileColor.Black))]
        };
        // The Red-8 moved from the group into a new run; its (value,color) key is unchanged.
        var after = new Solution
        {
            Runs =
            [
                Run(new Tile(4, TileColor.Red), new Tile(5, TileColor.Red), new Tile(6, TileColor.Red),
                    new Tile(7, TileColor.Red), new Tile(8, TileColor.Red))
            ],
            Groups = [Run(new Tile(8, TileColor.Blue), new Tile(8, TileColor.Black))]
        };

        BoardFlipKeys.Assign(after).Should().Contain("8-Red#0");
    }

    [Fact]
    public void Assign_EmptyBoard_ReturnsEmpty()
    {
        BoardFlipKeys.Assign(new Solution()).Should().BeEmpty();
    }
}
