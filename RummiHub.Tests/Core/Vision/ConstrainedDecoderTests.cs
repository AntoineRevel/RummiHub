using AwesomeAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Vision;

namespace RummiHub.Tests.Core.Vision;

public class ConstrainedDecoderTests
{
    private static readonly Tile Joker = new(isJoker: true);

    private static Tile B(int v) => new(v, TileColor.Blue);
    private static Tile R(int v) => new(v, TileColor.Red);
    private static Tile K(int v) => new(v, TileColor.Black);

    /// <summary>What a reader that hesitates between a few tiles would output.</summary>
    private static TileReading Seen(params (Tile Tile, float P)[] options)
    {
        var faces = new float[14];
        var colors = new float[4];
        foreach (var (tile, p) in options)
        {
            faces[tile.IsJoker ? TileReading.JokerFace : tile.Value - 1] += p;
            if (!tile.IsJoker) colors[(int)tile.Color] += p;
        }

        return new TileReading(faces, colors);
    }

    private static TileReading Sure(Tile tile) => TileReading.Certain(tile);

    private static IReadOnlyList<TileReading> SetOf(params Tile[] tiles) => tiles.Select(Sure).ToList();

    private static Tile[] TilesOf(ReadSet set) => set.Tiles.Select(t => t.Tile).ToArray();

    [Fact]
    public void Decode_ClearPhoto_ReadsItAsIs()
    {
        var game = ConstrainedDecoder.Decode(
            [SetOf(R(4), R(5), R(6)), SetOf(B(9), R(9), K(9))],
            [Sure(B(1)), Sure(K(13))]);

        game.NeedsReview.Should().BeFalse();
        TilesOf(game.Table[0]).Should().Equal(R(4), R(5), R(6));
        TilesOf(game.Table[1]).Should().Equal(B(9), R(9), K(9));
        game.ToHand().Should().Equal(B(1), K(13));
        game.Table.SelectMany(s => s.Tiles).Concat(game.Rack).Should().OnlyContain(t => !t.Corrected);
    }

    [Fact]
    public void Decode_GroupWithTheSameColorTwice_TakesTheReadersSecondChoice()
    {
        // The last 5 looks a bit more blue than black, but the group already has a blue 5.
        var game = ConstrainedDecoder.Decode(
            [[Sure(R(5)), Sure(B(5)), Seen((B(5), 0.55f), (K(5), 0.45f))]],
            []);

        TilesOf(game.Table[0]).Should().Equal(R(5), B(5), K(5));
        game.Table[0].Tiles[2].Corrected.Should().BeTrue();
        game.Table[0].Legal.Should().BeTrue();
    }

    [Fact]
    public void Decode_RunWithAMisreadValue_FillsTheGap()
    {
        var game = ConstrainedDecoder.Decode(
            [[Sure(B(4)), Sure(B(5)), Seen((B(8), 0.6f), (B(6), 0.3f), (B(9), 0.1f)), Sure(B(7))]],
            []);

        TilesOf(game.Table[0]).Should().Equal(B(4), B(5), B(6), B(7));
    }

    [Fact]
    public void Decode_RunPhotographedFromTheOtherSide_ReadsBackwards()
    {
        var game = ConstrainedDecoder.Decode([SetOf(K(12), K(11), K(10))], []);

        TilesOf(game.Table[0]).Should().Equal(K(12), K(11), K(10));
        game.Table[0].Legal.Should().BeTrue();
    }

    [Fact]
    public void Decode_JokerOnTheTable_StandsForItsSeat()
    {
        var game = ConstrainedDecoder.Decode([SetOf(Joker, R(5), R(6))], []);

        var joker = game.Table[0].Tiles[0].Tile;
        joker.IsJoker.Should().BeTrue();
        joker.Value.Should().Be(4);
        joker.Color.Should().Be(TileColor.Red);
        SetRules.IsLegal(game.ToBoard().Sets[0]).Should().BeTrue();
    }

    [Fact]
    public void Decode_JokerInAGroup_TakesAFreeColor()
    {
        var game = ConstrainedDecoder.Decode([SetOf(B(11), Joker, K(11))], []);

        var joker = game.Table[0].Tiles[1].Tile;
        joker.IsJoker.Should().BeTrue();
        joker.Value.Should().Be(11);
        joker.Color.Should().NotBe(TileColor.Blue).And.NotBe(TileColor.Black);
    }

    [Fact]
    public void Decode_ThirdCopyOfATile_GoesToTheReadersNextChoice()
    {
        // Both red 7s are on the table: the rack tile cannot be a third one.
        var game = ConstrainedDecoder.Decode(
            [SetOf(R(7), B(7), K(7)), SetOf(R(6), R(7), R(8))],
            [Seen((R(7), 0.6f), (R(1), 0.4f))]);

        game.ToHand().Should().Equal(R(1));
        game.Rack[0].Corrected.Should().BeTrue();
        game.WithinTileCounts.Should().BeTrue();
    }

    [Fact]
    public void Decode_ThirdJoker_GoesToTheReadersNextChoice()
    {
        var game = ConstrainedDecoder.Decode(
            [SetOf(Joker, R(5), R(6)), SetOf(B(2), Joker, B(4))],
            [Seen((Joker, 0.7f), (K(3), 0.3f))]);

        game.ToHand().Should().Equal(K(3));
    }

    [Fact]
    public void Decode_TwoTilesAlone_ArePassedOnForThePlayerToFix()
    {
        var game = ConstrainedDecoder.Decode([SetOf(R(3), R(4))], []);

        game.Table[0].Legal.Should().BeFalse();
        game.NeedsReview.Should().BeTrue();
        TilesOf(game.Table[0]).Should().Equal(R(3), R(4));
    }

    [Fact]
    public void Decode_SureTilesThatMakeNoSet_AreNotForcedIntoOne()
    {
        var game = ConstrainedDecoder.Decode([SetOf(R(3), R(5), R(9))], []);

        game.Table[0].Legal.Should().BeFalse();
        TilesOf(game.Table[0]).Should().Equal(R(3), R(5), R(9));
        game.Table[0].Tiles.Should().OnlyContain(t => !t.Corrected);
    }

    [Fact]
    public void Decode_MoreThanTwoCopiesWhateverTheReading_IsFlagged()
    {
        var game = ConstrainedDecoder.Decode([], [Sure(B(2)), Sure(B(2)), Sure(B(2))]);

        game.WithinTileCounts.Should().BeFalse();
        game.NeedsReview.Should().BeTrue();
        game.ToHand().Should().Equal(B(2), B(2), B(2));
    }

    [Fact]
    public void Decode_LongestRun_IsLegal()
    {
        var game = ConstrainedDecoder.Decode([SetOf(Enumerable.Range(1, 13).Select(B).ToArray())], []);

        game.Table[0].Legal.Should().BeTrue();
        TilesOf(game.Table[0]).Should().Equal(Enumerable.Range(1, 13).Select(B));
    }
}
