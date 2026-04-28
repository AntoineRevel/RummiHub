using FluentAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Contracts;

public class ContentHasherTests
{
    private static Solution EmptyBoard() => new();

    private static Solution BoardWithRun(params Tile[] tiles) =>
        new() { Runs = [new ValidSet(tiles)] };

    [Fact]
    public void SameInput_ReturnsSameHash()
    {
        var hand = new List<Tile> { new(1, TileColor.Blue), new(2, TileColor.Red) };
        var board = EmptyBoard();

        var hash1 = ContentHasher.ComputeHash(hand, board);
        var hash2 = ContentHasher.ComputeHash(hand, board);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void DifferentTileOrdering_ReturnsSameHash()
    {
        var hand1 = new List<Tile> { new(1, TileColor.Blue), new(2, TileColor.Red) };
        var hand2 = new List<Tile> { new(2, TileColor.Red), new(1, TileColor.Blue) };
        var board = EmptyBoard();

        var hash1 = ContentHasher.ComputeHash(hand1, board);
        var hash2 = ContentHasher.ComputeHash(hand2, board);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void DifferentHand_ReturnsDifferentHash()
    {
        var hand1 = new List<Tile> { new(1, TileColor.Blue) };
        var hand2 = new List<Tile> { new(2, TileColor.Blue) };
        var board = EmptyBoard();

        var hash1 = ContentHasher.ComputeHash(hand1, board);
        var hash2 = ContentHasher.ComputeHash(hand2, board);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void DifferentBoard_ReturnsDifferentHash()
    {
        var hand = new List<Tile> { new(1, TileColor.Blue) };
        var board1 = EmptyBoard();
        var board2 = BoardWithRun(new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue));

        var hash1 = ContentHasher.ComputeHash(hand, board1);
        var hash2 = ContentHasher.ComputeHash(hand, board2);

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Returns16CharLowercaseHex()
    {
        var hand = new List<Tile> { new(5, TileColor.Red) };
        var board = EmptyBoard();

        var hash = ContentHasher.ComputeHash(hand, board);

        hash.Should().HaveLength(16);
        hash.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Fact]
    public void WithJokers_IsConsistent()
    {
        var hand1 = new List<Tile> { new(isJoker: true), new(3, TileColor.Blue) };
        var hand2 = new List<Tile> { new(3, TileColor.Blue), new(isJoker: true) };
        var board = EmptyBoard();

        var hash1 = ContentHasher.ComputeHash(hand1, board);
        var hash2 = ContentHasher.ComputeHash(hand2, board);

        hash1.Should().Be(hash2);
    }
}
