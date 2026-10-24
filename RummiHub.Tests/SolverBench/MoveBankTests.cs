using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.SolverBench.Bank;
using RummiHub.SolverBench.Model;
using RummiHub.SolverBench.Trace;

namespace RummiHub.Tests.SolverBench;

public class MoveBankTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rummi-bank-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static BenchPosition SamplePosition()
    {
        var board = new Solution { IsValid = true };
        board.Sets.Add(new ValidSet([new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue)]));
        board.Sets.Add(new ValidSet([new Tile(5, TileColor.Red), new Tile(5, TileColor.Black), new Tile(5, TileColor.Mango)]));
        var hand = new List<Tile> { new(13, TileColor.Blue), new(isJoker: true) };

        return new BenchPosition
        {
            Id = BenchPosition.ComputeId(hand, board, hasPlayed: true),
            Name = "sample",
            Tags = ["during-game", "joker-play"],
            HasPlayed = true,
            Board = board,
            Hand = hand,
            Provenance = "test",
            ReferenceBest = new ReferenceBest { Found = true, Tiles = [new(13, TileColor.Blue)], Jokers = 1, Score = 25 },
            Notes = "round-trip check"
        };
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var bank = new MoveBank(_dir);
        var original = SamplePosition();

        bank.Save(original);
        var loaded = bank.Load().Single();

        loaded.Id.Should().Be(original.Id);
        loaded.Name.Should().Be(original.Name);
        loaded.Tags.Should().Equal(original.Tags);
        loaded.HasPlayed.Should().BeTrue();
        loaded.Provenance.Should().Be(original.Provenance);
        loaded.Notes.Should().Be(original.Notes);
        loaded.Hand.Should().BeEquivalentTo(original.Hand);
        loaded.Board.Sets.Select(s => s.IsGroup).Should().Equal(original.Board.Sets.Select(s => s.IsGroup));
        loaded.ReferenceBest.Found.Should().BeTrue();
        loaded.ReferenceBest.Jokers.Should().Be(1);
        loaded.ReferenceBest.Score.Should().Be(25);
        loaded.ReferenceBest.Tiles.Should().BeEquivalentTo(original.ReferenceBest.Tiles);
    }

    [Fact]
    public void Id_RoundTrips_ToSameContentHash()
    {
        var bank = new MoveBank(_dir);
        var original = SamplePosition();
        bank.Save(original);

        var loaded = bank.Load().Single();
        BenchPosition.ComputeId(loaded.Hand, loaded.Board, loaded.HasPlayed).Should().Be(original.Id);
    }

    [Fact]
    public void TryAdd_IsIdempotent_ForSameId()
    {
        var bank = new MoveBank(_dir);
        var position = SamplePosition();

        bank.TryAdd(position).Should().BeTrue();
        bank.TryAdd(position).Should().BeFalse("the id is already present");
        bank.Load().Should().HaveCount(1);
    }

    [Fact]
    public void Load_SkipsMalformedFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not valid json ");
        new MoveBank(_dir).Save(SamplePosition());

        new MoveBank(_dir).Load().Should().HaveCount(1, "the malformed file is ignored, the valid one loads");
    }

    [Fact]
    public void TraceCorpus_RoundTripsThroughBank()
    {
        var bank = new MoveBank(_dir);
        var positions = GameTraceGenerator.Generate(2024, new GameTraceOptions { MaxPositions = 12 }).Positions;
        foreach (var p in positions) bank.Save(p);

        var loaded = bank.Load();
        loaded.Select(p => p.Id).Should().BeEquivalentTo(positions.Select(p => p.Id).Distinct());
    }
}
