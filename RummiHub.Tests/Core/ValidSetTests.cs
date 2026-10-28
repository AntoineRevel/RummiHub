using AwesomeAssertions;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class ValidSetTests
{
    [Fact]
    public void GetScore_SumsNonJokerValues()
    {
        var set = new ValidSet([
            new Tile(3, TileColor.Blue),
            new Tile(4, TileColor.Red),
            new Tile(5, TileColor.Black)
        ]);

        set.GetScore().Should().Be(12);
    }

    [Fact]
    public void GetScore_JokersContributeZero()
    {
        var set = new ValidSet([
            new Tile(3, TileColor.Blue),
            new Tile(isJoker: true),
            new Tile(5, TileColor.Black)
        ]);

        set.GetScore().Should().Be(8);
    }

    [Fact]
    public void GetScore_EmptySet_ReturnsZero()
    {
        var set = new ValidSet([]);

        set.GetScore().Should().Be(0);
    }
}
