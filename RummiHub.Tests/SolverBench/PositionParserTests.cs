using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.SolverBench;
using RummiHub.SolverBench.Model;

namespace RummiHub.Tests.SolverBench;

public class PositionParserTests
{
    [Theory]
    [InlineData("10B", 10, TileColor.Blue)]
    [InlineData("7r", 7, TileColor.Red)]
    [InlineData("13M", 13, TileColor.Mango)]
    [InlineData("5k", 5, TileColor.Black)]
    public void ParseTile_ReadsValueAndColor(string token, int value, TileColor color)
    {
        var tile = PositionParser.ParseTile(token);
        tile.IsJoker.Should().BeFalse();
        tile.Value.Should().Be(value);
        tile.Color.Should().Be(color);
    }

    [Theory]
    [InlineData("J")]
    [InlineData("*")]
    public void ParseTile_ReadsJoker(string token) =>
        PositionParser.ParseTile(token).IsJoker.Should().BeTrue();

    [Fact]
    public void ParseHand_SplitsOnCommasAndSpaces()
    {
        var hand = PositionParser.ParseHand("10B, 11B 12B,J");
        hand.Should().HaveCount(4);
        hand.Count(t => t.IsJoker).Should().Be(1);
    }

    [Theory]
    [InlineData("14B")]   // value out of range
    [InlineData("10Z")]   // bad colour
    [InlineData("xx")]
    public void ParseTile_RejectsInvalidTokens(string token) =>
        FluentAssertions.FluentActions.Invoking(() => PositionParser.ParseTile(token))
            .Should().Throw<FormatException>();

    [Fact]
    public void FirstPlayFromHand_BuildsOpeningPositionWithReference()
    {
        var position = PositionParser.FirstPlayFromHand("10B,11B,12B", "my opening");

        position.Phase.Should().Be(BenchPhase.FirstPlay);
        position.HasPlayed.Should().BeFalse();
        position.Provenance.Should().Be("manual");
        position.ReferenceBest.Found.Should().BeTrue("[10B,11B,12B] scores 33 ≥ 30");
        position.ReferenceBest.Score.Should().BeGreaterThanOrEqualTo(30);
        position.Id.Should().Be(BenchPosition.ComputeId(position.Hand, position.Board, false));
    }

    [Fact]
    public void FirstPlayFromHand_EmptyHand_Throws() =>
        FluentAssertions.FluentActions.Invoking(() => PositionParser.FirstPlayFromHand(""))
            .Should().Throw<FormatException>();
}
