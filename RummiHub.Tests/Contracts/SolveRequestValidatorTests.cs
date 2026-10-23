using FluentAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Contracts;

public class SolveRequestValidatorTests
{
    private static SolveRequest Request(IEnumerable<Tile> hand, Solution? board = null, string requestId = "req-1") =>
        new()
        {
            RequestId = requestId,
            Hand = hand.ToList(),
            Board = board ?? new Solution(),
            HasPlayed = true
        };

    [Fact]
    public void TryValidate_AcceptsALegalPosition()
    {
        var board = new Solution
        {
            Runs =
            [
                new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)])
            ]
        };
        var hand = new List<Tile> { new(1, TileColor.Red), new(1, TileColor.Red), new Tile(isJoker: true) };

        SolveRequestValidator.TryValidate(Request(hand, board), out var error).Should().BeTrue();
        error.Should().BeEmpty();
    }

    [Fact]
    public void TryValidate_RejectsMoreThanTwoJokers_IncludingJokersThatStillCarryAValue()
    {
        var hand = new List<Tile>
        {
            new(isJoker: true),
            new(isJoker: true),
            new(5, TileColor.Red, isJoker: true)
        };

        SolveRequestValidator.TryValidate(Request(hand), out var error).Should().BeFalse();
        error.Should().Contain("2 jokers");
    }

    [Fact]
    public void TryValidate_RejectsAThirdCopyAcrossHandAndBoard()
    {
        var board = new Solution
        {
            Groups =
            [
                new ValidSet([new Tile(7, TileColor.Black), new Tile(7, TileColor.Black)])
            ]
        };
        var hand = new List<Tile> { new(7, TileColor.Black) };

        SolveRequestValidator.TryValidate(Request(hand, board), out var error).Should().BeFalse();
        error.Should().Contain("at most 2");
    }

    [Fact]
    public void TryValidate_AllowsTwoCopiesOfTheSameTile()
    {
        var hand = new List<Tile> { new(4, TileColor.Mango), new(4, TileColor.Mango) };

        SolveRequestValidator.TryValidate(Request(hand), out _).Should().BeTrue();
    }

    [Fact]
    public void TryValidate_RejectsADefaultTile()
    {
        // default(Tile) is the one non-joker with value 0 that the constructor still allows.
        var hand = new List<Tile> { default };

        SolveRequestValidator.TryValidate(Request(hand), out var error).Should().BeFalse();
        error.Should().Contain("1 to 13");
    }

    [Fact]
    public void TryValidate_RejectsAnOversizedHandBeforeCountingJokers()
    {
        var hand = Enumerable.Repeat(new Tile(isJoker: true), SolveRequestValidator.MaxTiles + 1);

        SolveRequestValidator.TryValidate(Request(hand), out var error).Should().BeFalse();
        error.Should().Contain("Hand cannot exceed");
    }

    [Fact]
    public void TryValidate_RejectsHandAndBoardThatTogetherExceedTheSet()
    {
        var runs = new List<ValidSet>();
        var remaining = 50;
        while (remaining > 0)
        {
            var take = Math.Min(SolveRequestValidator.MaxTilesPerSet, remaining);
            runs.Add(new ValidSet(Enumerable.Repeat(new Tile(isJoker: true), take).ToArray()));
            remaining -= take;
        }

        var hand = Enumerable.Repeat(new Tile(isJoker: true), 60);
        var board = new Solution { Runs = runs };

        SolveRequestValidator.TryValidate(Request(hand, board), out var error).Should().BeFalse();
        error.Should().Contain("together");
    }

    [Fact]
    public void TryValidate_RejectsASetLongerThanARun()
    {
        var board = new Solution
        {
            Runs = [new ValidSet(Enumerable.Range(1, 14).Select(v => new Tile(Math.Min(v, 13), TileColor.Blue)).ToArray())]
        };

        SolveRequestValidator.TryValidate(Request([], board), out var error).Should().BeFalse();
        error.Should().Contain("cannot exceed 13");
    }

    [Fact]
    public void TryValidate_RejectsTooManySets()
    {
        var board = new Solution
        {
            Groups = Enumerable.Range(0, SolveRequestValidator.MaxSets + 1).Select(_ => new ValidSet()).ToList()
        };

        SolveRequestValidator.TryValidate(Request([], board), out var error).Should().BeFalse();
        error.Should().Contain("groups");
    }

    [Fact]
    public void TryValidate_RejectsANullSet()
    {
        var board = new Solution { Runs = [null!] };

        SolveRequestValidator.TryValidate(Request([], board), out var error).Should().BeFalse();
        error.Should().Contain("empty set");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_RejectsABlankRequestId(string requestId)
    {
        SolveRequestValidator.TryValidate(Request([new Tile(1, TileColor.Blue)], requestId: requestId), out var error)
            .Should().BeFalse();
        error.Should().Contain("RequestId");
    }

    [Fact]
    public void TryValidate_RejectsAnOverlongRequestId()
    {
        var requestId = new string('a', SolveRequestValidator.MaxRequestIdLength + 1);

        SolveRequestValidator.TryValidate(Request([new Tile(1, TileColor.Blue)], requestId: requestId), out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryValidate_RejectsAMissingHand()
    {
        var request = new SolveRequest { RequestId = "x", Hand = null!, Board = new Solution() };

        SolveRequestValidator.TryValidate(request, out var error).Should().BeFalse();
        error.Should().Contain("Hand");
    }

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("0123456789abcde", false)]
    [InlineData("0123456789ABCDEFF", false)]
    [InlineData("0123456789abcdeg", false)]
    [InlineData("../etc/passwdxx", false)]
    [InlineData(null, false)]
    public void IsContentHash_AcceptsOnly16LowerHexChars(string? hash, bool expected)
    {
        SolveRequestValidator.IsContentHash(hash).Should().Be(expected);
    }
}
