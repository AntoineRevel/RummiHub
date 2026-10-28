using AwesomeAssertions;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class SetTests
{
    [Fact]
    public void EmptyConstructor_StartsEmpty()
    {
        var set = new Set();

        set.Tiles.Should().BeEmpty();
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void ListConstructor_WithNoJokers_PopulatesTiles()
    {
        var tiles = new List<Tile> { new(1, TileColor.Blue), new(2, TileColor.Red) };
        var set = new Set(tiles);

        set.Tiles.Should().HaveCount(2);
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void ListConstructor_MovesJokersToCounter()
    {
        var tiles = new List<Tile>
        {
            new(1, TileColor.Blue),
            new(isJoker: true),
            new(3, TileColor.Red),
            new(isJoker: true)
        };
        var set = new Set(tiles);

        set.Tiles.Should().HaveCount(2);
        set.Jokers.Should().Be(2);
        set.Tiles.Should().NotContain(t => t.IsJoker);
    }

    [Fact]
    public void CopyConstructor_DeepCopies()
    {
        var original = new Set();
        original.AddTile(new Tile(5, TileColor.Blue));
        original.AddTile(new Tile(isJoker: true));

        var copy = new Set(original);
        copy.AddTile(new Tile(6, TileColor.Red));

        original.Tiles.Should().HaveCount(1);
        copy.Tiles.Should().HaveCount(2);
    }

    [Fact]
    public void AddTile_NonJoker_AddedToTiles()
    {
        var set = new Set();
        var tile = new Tile(3, TileColor.Blue);

        set.AddTile(tile);

        set.Tiles.Should().ContainSingle();
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void AddTile_Joker_IncreasesJokersCounter()
    {
        var set = new Set();

        set.AddTile(new Tile(isJoker: true));
        set.AddTile(new Tile(isJoker: true));

        set.Jokers.Should().Be(2);
        set.Tiles.Should().BeEmpty();
    }

    [Fact]
    public void Remove_NonJoker_RemovesFromTiles()
    {
        var tile = new Tile(4, TileColor.Mango);
        var set = new Set();
        set.AddTile(tile);

        var removed = set.Remove(tile);

        removed.Should().BeTrue();
        set.Tiles.Should().BeEmpty();
    }

    [Fact]
    public void Remove_MissingTile_ReturnsFalseAndLeavesSetUnchanged()
    {
        var kept = new Tile(4, TileColor.Mango);
        var set = new Set([kept]);

        var removed = set.Remove(new Tile(4, TileColor.Blue));

        removed.Should().BeFalse();
        set.Tiles.Should().ContainSingle().Which.Should().Be(kept);
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void Remove_Duplicate_RemovesOneCopyAtATime()
    {
        var tile = new Tile(8, TileColor.Red);
        var set = new Set([tile, tile]);

        set.Remove(tile).Should().BeTrue();
        set.Tiles.Should().ContainSingle().Which.Should().Be(tile);

        set.Remove(tile).Should().BeTrue();
        set.Remove(tile).Should().BeFalse();
        set.Tiles.Should().BeEmpty();
    }

    [Fact]
    public void Remove_Joker_DecrementsCounter()
    {
        var set = new Set();
        set.AddTile(new Tile(isJoker: true));

        var removed = set.Remove(new Tile(isJoker: true));

        removed.Should().BeTrue();
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void Remove_JokerWhenNone_DoesNotGoNegative()
    {
        var set = new Set();

        var removed = set.Remove(new Tile(isJoker: true));

        removed.Should().BeFalse();
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void ContainsAll_CountsDuplicatesAndJokers()
    {
        var blue = new Tile(1, TileColor.Blue);
        var red = new Tile(1, TileColor.Red);
        var set = new Set([blue, blue, red, new Tile(isJoker: true)]);

        set.ContainsAll([]).Should().BeTrue();
        set.ContainsAll([blue, red]).Should().BeTrue();
        set.ContainsAll([blue, blue]).Should().BeTrue();
        set.ContainsAll([blue, blue, blue]).Should().BeFalse();
        set.ContainsAll([new Tile(isJoker: true)]).Should().BeTrue();
        set.ContainsAll([new Tile(13, TileColor.Black, isJoker: true)]).Should().BeTrue();
        set.ContainsAll([new Tile(isJoker: true), new Tile(isJoker: true)]).Should().BeFalse();
        set.ContainsAll([new Tile(1, TileColor.Black)]).Should().BeFalse();

        set.Tiles.Should().HaveCount(3);
        set.Jokers.Should().Be(1);
    }

    [Fact]
    public void Concat_MergesTilesAndJokers()
    {
        var set = new Set();
        set.AddTile(new Tile(1, TileColor.Blue));

        var validSet = new ValidSet([
            new Tile(2, TileColor.Red),
            new Tile(isJoker: true)
        ]);

        set.Concat(validSet);

        set.Tiles.Should().HaveCount(2);
        set.Jokers.Should().Be(1);
    }

    [Fact]
    public void GetAllTilesIncludingJokers_YieldsCorrectCount()
    {
        var set = new Set();
        set.AddTile(new Tile(1, TileColor.Blue));
        set.AddTile(new Tile(2, TileColor.Red));
        set.AddTile(new Tile(isJoker: true));

        var all = set.GetAllTilesIncludingJokers().ToList();

        all.Should().HaveCount(3);
        all.Count(t => t.IsJoker).Should().Be(1);
    }
}
