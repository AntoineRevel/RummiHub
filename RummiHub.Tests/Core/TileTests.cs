using System.Text.Json;
using FluentAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class TileTests
{
    [Theory]
    [InlineData(1, TileColor.Blue)]
    [InlineData(13, TileColor.Black)]
    [InlineData(7, TileColor.Red)]
    [InlineData(5, TileColor.Mango)]
    public void Constructor_StoresValueAndColor(int value, TileColor color)
    {
        var tile = new Tile(value, color);

        tile.Value.Should().Be(value);
        tile.Color.Should().Be(color);
        tile.IsJoker.Should().BeFalse();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(100)]
    public void Constructor_ThrowsOnValueOutOfRange(int value)
    {
        var act = () => new Tile(value, TileColor.Blue);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(13)]
    public void Constructor_JokerMayStandForZeroThrough13(int value)
    {
        var tile = new Tile(value, TileColor.Red, isJoker: true);

        tile.IsJoker.Should().BeTrue();
        tile.Value.Should().Be(value);
        tile.Color.Should().Be(TileColor.Red);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(14)]
    [InlineData(15)]
    public void Constructor_RejectsJokerValuesOutsideZeroThrough13(int value)
    {
        var act = () => new Tile(value, TileColor.Blue, isJoker: true);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidColor()
    {
        var act = () => new Tile(5, (TileColor)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void JokerConstructor_SetsIsJokerTrue()
    {
        var tile = new Tile(isJoker: true);

        tile.IsJoker.Should().BeTrue();
    }

    [Fact]
    public void IsNull_TrueForDefault()
    {
        var tile = default(Tile);

        tile.IsNull.Should().BeTrue();
    }

    [Fact]
    public void IsNull_FalseForNonDefault()
    {
        var tile = new Tile(1, TileColor.Blue);

        tile.IsNull.Should().BeFalse();
    }

    [Fact]
    public void Equals_TwoJokersAreEqual()
    {
        var joker1 = new Tile(isJoker: true);
        var joker2 = new Tile(isJoker: true);

        joker1.Should().Be(joker2);
        (joker1 == joker2).Should().BeTrue();
    }

    [Fact]
    public void Equals_JokerNotEqualToNonJoker()
    {
        var joker = new Tile(isJoker: true);
        var normal = new Tile(1, TileColor.Blue);

        (joker == normal).Should().BeFalse();
    }

    [Fact]
    public void Equals_SameTiles_AreEqual()
    {
        var a = new Tile(5, TileColor.Red);
        var b = new Tile(5, TileColor.Red);

        a.Should().Be(b);
    }

    [Fact]
    public void Equals_DifferentTiles_AreNotEqual()
    {
        var a = new Tile(5, TileColor.Red);
        var b = new Tile(5, TileColor.Blue);

        (a == b).Should().BeFalse();
    }

    [Fact]
    public void GetHashCode_AllJokersReturnSameHash()
    {
        var joker1 = new Tile(isJoker: true);
        var joker2 = new Tile(isJoker: true);

        joker1.GetHashCode().Should().Be(joker2.GetHashCode());
        joker1.GetHashCode().Should().Be(0xFF);
    }

    [Fact]
    public void ToString_Joker_ReturnsJokerString()
    {
        var tile = new Tile(isJoker: true);

        tile.ToString().Should().Be("Joker");
    }

    [Fact]
    public void ToString_NonJoker_ReturnsColorAndValue()
    {
        var tile = new Tile(7, TileColor.Red);

        tile.ToString().Should().Be("Red 7");
    }

    [Fact]
    public void JsonSerialization_NonJoker_Roundtrip()
    {
        var original = new Tile(9, TileColor.Mango);
        var json = JsonSerializer.Serialize(original, RummiJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<Tile>(json, RummiJsonOptions.Default);

        deserialized.Should().Be(original);
        deserialized.Value.Should().Be(9);
        deserialized.Color.Should().Be(TileColor.Mango);
    }

    [Fact]
    public void JsonSerialization_Joker_Roundtrip()
    {
        var original = new Tile(isJoker: true);
        var json = JsonSerializer.Serialize(original, RummiJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<Tile>(json, RummiJsonOptions.Default);

        deserialized.IsJoker.Should().BeTrue();
    }

    [Fact]
    public void JsonSerialization_BoardJoker_KeepsTheTileItStandsFor()
    {
        var original = new Tile(13, TileColor.Mango, isJoker: true);
        var json = JsonSerializer.Serialize(original, RummiJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<Tile>(json, RummiJsonOptions.Default);

        deserialized.IsJoker.Should().BeTrue();
        deserialized.Value.Should().Be(13);
        deserialized.Color.Should().Be(TileColor.Mango);
    }

    [Fact]
    public void JsonSerialization_JokerAbove13_DoesNotKeepThatFace()
    {
        const string json = """{"value":14,"color":"Blue","isJoker":true}""";

        var tile = JsonSerializer.Deserialize<Tile>(json, RummiJsonOptions.Default);

        tile.IsJoker.Should().BeTrue();
        tile.Value.Should().Be(0);
    }

    [Fact]
    public void JsonSerialization_NonJokerOutside1To13_Throws()
    {
        const string json = """{"value":14,"color":"Blue","isJoker":false}""";

        var act = () => JsonSerializer.Deserialize<Tile>(json, RummiJsonOptions.Default);

        act.Should().Throw<JsonException>();
    }
}
