using AwesomeAssertions;
using RummiHub.Core.Models;
using RummiHub.Web.Models;

namespace RummiHub.Tests.Web;

public class BoardLayoutsTests
{
    private static ValidSet Run(TileColor color, int from, int to) =>
        new(Enumerable.Range(from, to - from + 1).Select(value => new Tile(value, color)).ToArray());

    [Fact]
    public void Snapshot_KeepsOneLayoutPerWidthInUse_AndMovesThemWithTheBoard()
    {
        var layouts = new BoardLayouts();
        var a = Run(TileColor.Red, 1, 3);
        var b = Run(TileColor.Blue, 1, 3);
        layouts.Sync([a, b]);
        layouts.Use("desktop", 44);
        layouts.Use("phone", 12);

        var before = layouts.Snapshot();
        before[44].Should().Equal(0, 0, 0, 9);
        before[12].Should().Equal(0, 0, 1, 0);

        // A third set is played: the first two stay put at both widths.
        var c = Run(TileColor.Black, 1, 3);
        layouts.Sync([a, b, c]);

        layouts.Snapshot()[12].Should().Equal(0, 0, 1, 0, 2, 0);
        layouts.Snapshot()[44].Should().Equal(0, 0, 0, 9, 0, 18);
    }

    [Fact]
    public void Release_KeepsTheLayout_SoAReloadFindsTheTableAsItWas()
    {
        var layouts = new BoardLayouts();
        var a = Run(TileColor.Red, 1, 3);
        var b = Run(TileColor.Blue, 1, 3);
        var c = Run(TileColor.Black, 1, 3);
        layouts.Use("page", 20);
        layouts.Sync([a, b, c]);
        layouts.Sync([a, c]); // leaves a hole a fresh layout would not have

        var before = layouts.Snapshot()[20];
        layouts.Release("page");
        layouts.Use("page-again", 20);

        layouts.Snapshot()[20].Should().Equal(before);
    }

    [Fact]
    public void Use_DropsOnlyLayoutsNoPageUses_WhenTooManyWidthsPileUp()
    {
        var layouts = new BoardLayouts();
        layouts.Use("stays", 20);
        for (var columns = 30; columns < 60; columns++) layouts.Use("resizing", columns);

        var kept = layouts.Snapshot();
        kept.Should().ContainKey(20);
        kept.Should().ContainKey(59);
        kept.Count.Should().BeLessThanOrEqualTo(12);
    }
}
