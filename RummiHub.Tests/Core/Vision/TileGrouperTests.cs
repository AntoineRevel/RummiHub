using AwesomeAssertions;
using RummiHub.Core.Vision;

namespace RummiHub.Tests.Core.Vision;

public class TileGrouperTests
{
    private const float W = 40, H = 56;

    /// <summary>A row of tiles side by side, starting at (x, y), with a small gap between them.</summary>
    private static IEnumerable<TileBox> Row(float x, float y, int count, float tilt = 0) =>
        Enumerable.Range(0, count).Select(i => new TileBox(x + i * (W + 2), y + i * tilt, W, H));

    [Fact]
    public void Group_NoTiles_NoSets()
    {
        TileGrouper.Group([]).Should().BeEmpty();
    }

    [Fact]
    public void Group_SetsApart_InReadingOrder()
    {
        // Second row first in the list, and the right-hand set of the top row before the left.
        var boxes = Row(0, 200, 4).Concat(Row(300, 0, 3)).Concat(Row(0, 0, 3)).ToList();

        var sets = TileGrouper.Group(boxes);

        sets.Should().HaveCount(3);
        sets[0].Should().Equal(7, 8, 9);       // top row, left
        sets[1].Should().Equal(4, 5, 6);       // top row, right
        sets[2].Should().Equal(0, 1, 2, 3);    // bottom row
    }

    [Fact]
    public void Group_TiltedPhoto_KeepsTheSetWhole()
    {
        // Each tile sits a quarter of a tile lower than the previous: the last one is three
        // tiles below the first, but every neighbour is level enough.
        var boxes = Row(0, 0, 13, tilt: H / 4).ToList();

        TileGrouper.Group(boxes).Should().ContainSingle().Which.Should().HaveCount(13);
    }

    [Fact]
    public void Group_TilesListedOutOfOrder_ComeBackLeftToRight()
    {
        var boxes = Row(0, 0, 4).Reverse().ToList();

        TileGrouper.Group(boxes).Should().ContainSingle().Which.Should().Equal(3, 2, 1, 0);
    }

    [Fact]
    public void Group_RowsCloseTogether_StaySeparate()
    {
        // Two sets one above the other, a few pixels apart.
        var boxes = Row(0, 0, 3).Concat(Row(0, H + 4, 3)).ToList();

        TileGrouper.Group(boxes).Should().HaveCount(2);
    }

    [Fact]
    public void ReadRack_TwoRows_TopRowFirst()
    {
        var boxes = Row(0, H + 10, 2).Concat(Row(0, 0, 2)).ToList();

        TileGrouper.ReadRack(boxes).Should().Equal(2, 3, 0, 1);
    }
}
