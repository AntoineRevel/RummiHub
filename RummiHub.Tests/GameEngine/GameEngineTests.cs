using FluentAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class GameEngineTests
{
    private static GameEngine CreateEngine(int playerCount = 2, string? seed = null)
    {
        var engine = new GameEngine();
        var names = Enumerable.Range(1, playerCount).Select(i => $"Player{i}").ToList();
        engine.Initialize(names, seed);
        return engine;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Initialize_TotalTilesIs106(int playerCount)
    {
        var engine = CreateEngine(playerCount);

        var rackTotal = engine.Players.Sum(p => p.Rack.Tiles.Count + p.Rack.Jokers);
        var total = rackTotal + engine.TilePoolCount;

        total.Should().Be(106);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Initialize_Distributes14TilesPerPlayer(int playerCount)
    {
        var engine = CreateEngine(playerCount);

        foreach (var player in engine.Players)
        {
            var count = player.Rack.Tiles.Count + player.Rack.Jokers;
            count.Should().Be(14);
        }
    }

    [Fact]
    public void Initialize_WithoutSeed_DealsDifferentlyAcrossGames()
    {
        var signatures = Enumerable.Range(0, 4)
            .Select(_ => string.Join(',', CreateEngine(seed: null).Players[0].Rack.GetAllTilesIncludingJokers()))
            .ToList();

        signatures.Distinct().Count().Should().BeGreaterThan(1);
    }

    [Fact]
    public void Initialize_RejectsMoreThanFourPlayers_BeforeDealing()
    {
        var engine = new GameEngine();
        var names = Enumerable.Range(1, 8).Select(i => $"P{i}").ToList();

        var act = () => engine.Initialize(names, "audit-seed");

        act.Should().Throw<ArgumentOutOfRangeException>();
        engine.Players.Should().BeEmpty();
        engine.TilePoolCount.Should().Be(0);
    }

    [Fact]
    public void Initialize_WithSameSeed_IsDeterministic()
    {
        var engine1 = CreateEngine(seed: "test-seed");
        var engine2 = CreateEngine(seed: "test-seed");

        var rack1 = engine1.Players[0].Rack.GetAllTilesIncludingJokers().ToList();
        var rack2 = engine2.Players[0].Rack.GetAllTilesIncludingJokers().ToList();

        rack1.Should().BeEquivalentTo(rack2, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Initialize_ClearsStateOnReinit()
    {
        var engine = CreateEngine(seed: "seed-a");
        var rack1 = engine.Players[0].Rack.GetAllTilesIncludingJokers().ToList();

        engine.Initialize(["Alice", "Bob"], "seed-b");
        var rack2 = engine.Players[0].Rack.GetAllTilesIncludingJokers().ToList();

        rack1.Should().NotBeEquivalentTo(rack2, o => o.WithStrictOrdering());
        engine.Turn.Should().Be(0);
        engine.IsGameOver.Should().BeFalse();
    }

    [Fact]
    public void Initialize_StartsWithPlayer0()
    {
        var engine = CreateEngine();

        engine.PlayerIndex.Should().Be(0);
        engine.CurrentPlayer.Name.Should().Be("Player1");
    }

    [Fact]
    public void Initialize_AssignsAutoFlagsPerSeat()
    {
        var engine = new GameEngine();

        engine.Initialize(["Human", "Bot"], "seed", autoPlayers: [false, true]);

        engine.Players[0].IsAuto.Should().BeFalse();
        engine.Players[1].IsAuto.Should().BeTrue();
    }

    [Fact]
    public void Initialize_WithoutAutoFlags_DefaultsToNoAutoPlayers()
    {
        var engine = CreateEngine();

        engine.Players.Should().OnlyContain(p => !p.IsAuto);
    }

    [Fact]
    public void SolveRequests_NameTheGameTurnAndSeat()
    {
        var engine = new GameEngine { GameId = "f40b52ef" };
        engine.Initialize(["Alice", "Bob"], "seed");
        engine.AdvanceToNextPlayer();

        engine.BuildSolveRequest().RequestId.Should().Be("game-f40b52ef-t0-p1");
        engine.BuildValidateRequest([]).RequestId.Should().Be("validate-f40b52ef-t0-p1");
    }

    [Fact]
    public void AdvanceToNextPlayer_CyclesThroughPlayers()
    {
        var engine = CreateEngine(3);

        engine.AdvanceToNextPlayer();
        engine.PlayerIndex.Should().Be(1);

        engine.AdvanceToNextPlayer();
        engine.PlayerIndex.Should().Be(2);

        engine.AdvanceToNextPlayer();
        engine.PlayerIndex.Should().Be(0);
    }

    [Fact]
    public void AdvanceToNextPlayer_IncrementsTurnOnWrapAround()
    {
        var engine = CreateEngine(2);

        engine.Turn.Should().Be(0);

        engine.AdvanceToNextPlayer(); // → Player2
        engine.Turn.Should().Be(0);

        engine.AdvanceToNextPlayer(); // → Player1 (wraps)
        engine.Turn.Should().Be(1);
    }

    [Fact]
    public void DrawTile_DecreasesPoolByOne()
    {
        var engine = CreateEngine();
        var before = engine.TilePoolCount;

        engine.DrawTile();

        engine.TilePoolCount.Should().Be(before - 1);
    }

    [Fact]
    public void DrawTile_AddsTileToCurrentPlayerRack()
    {
        var engine = CreateEngine();
        var before = engine.CurrentPlayer.Rack.Tiles.Count + engine.CurrentPlayer.Rack.Jokers;

        engine.DrawTile();

        var after = engine.CurrentPlayer.Rack.Tiles.Count + engine.CurrentPlayer.Rack.Jokers;
        after.Should().Be(before + 1);
    }

    [Fact]
    public void ApplySolveResult_NotFound_DoesNothing()
    {
        var engine = CreateEngine();
        var boardBefore = engine.Board;
        var rackBefore = engine.CurrentPlayer.Rack.Tiles.Count;

        var response = new SolveResponse { RequestId = "r1", Found = false };
        engine.ApplySolveResult(response);

        engine.Board.Should().BeSameAs(boardBefore);
        engine.CurrentPlayer.Rack.Tiles.Count.Should().Be(rackBefore);
        engine.IsGameOver.Should().BeFalse();
    }

    [Fact]
    public void ApplySolveResult_RemovesTilesFromRack()
    {
        var engine = CreateEngine(seed: "stable");
        var player = engine.CurrentPlayer;
        var playedTile = player.Rack.Tiles[0];
        var rackBefore = player.Rack.Tiles.Count;

        var newBoard = new Solution { IsValid = true };
        newBoard.AddRun(new ValidSet([playedTile, new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]));

        var response = new SolveResponse
        {
            RequestId = "r1",
            Found = true,
            NewBoard = newBoard,
            TilesToPlay = [playedTile],
            JokersToPlay = 0
        };

        engine.ApplySolveResult(response);

        player.Rack.Tiles.Count.Should().Be(rackBefore - 1);
    }

    [Fact]
    public void ApplySolveResult_FirstPlay_SetsHasPlayedTrue()
    {
        var engine = CreateEngine(seed: "stable");
        var player = engine.CurrentPlayer;
        player.HasPlayed.Should().BeFalse();

        var newBoard = new Solution { IsValid = true };
        var response = new SolveResponse
        {
            RequestId = "r1",
            Found = true,
            NewBoard = newBoard,
            TilesToPlay = []
        };

        engine.ApplySolveResult(response);

        player.HasPlayed.Should().BeTrue();
    }

    [Fact]
    public void ApplySolveResult_FirstPlay_MergesWithExistingBoard()
    {
        var engine = CreateEngine(seed: "stable");
        var existing = new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]);
        engine.Board.AddRun(existing);

        var newBoard = new Solution { IsValid = true };
        newBoard.AddGroup(new ValidSet([new Tile(5, TileColor.Red), new Tile(5, TileColor.Black), new Tile(5, TileColor.Mango)]));

        var response = new SolveResponse
        {
            RequestId = "r1",
            Found = true,
            NewBoard = newBoard,
            TilesToPlay = []
        };

        engine.ApplySolveResult(response);

        engine.Board.Runs.Should().HaveCount(1);
        engine.Board.Groups.Should().HaveCount(1);
    }

    [Fact]
    public void ApplySolveResult_EmptyRack_SetsIsGameOver()
    {
        var engine = CreateEngine(seed: "stable");
        var player = engine.CurrentPlayer;

        // Empty the rack manually (tiles then jokers)
        foreach (var tile in player.Rack.Tiles.ToList())
            player.Rack.Remove(tile);
        while (player.Rack.Jokers > 0)
            player.Rack.Remove(new Tile(isJoker: true));

        var newBoard = new Solution { IsValid = true };
        var response = new SolveResponse
        {
            RequestId = "r1",
            Found = true,
            NewBoard = newBoard,
            TilesToPlay = []
        };

        engine.ApplySolveResult(response);

        engine.IsGameOver.Should().BeTrue();
        engine.WinnerName.Should().Be(player.Name);
    }

    // ========== SOLVE REQUEST PRIORITY ==========

    [Fact]
    public void BuildValidateRequest_IsPrioritized()
    {
        var engine = CreateEngine();
        var selected = new List<Tile> { new(5, TileColor.Red), new(6, TileColor.Red) };

        var request = engine.BuildValidateRequest(selected);

        request.Priority.Should().BeTrue();
        request.Hand.Should().BeEquivalentTo(selected);
    }

    [Fact]
    public void BuildSolveRequest_IsNotPrioritized()
    {
        var engine = CreateEngine();

        var request = engine.BuildSolveRequest();

        request.Priority.Should().BeFalse();
        request.Hand.Should().BeEquivalentTo(
            engine.CurrentPlayer.Rack.GetAllTilesIncludingJokers());
    }

    [Fact]
    public void BuildValidateRequest_PropagatesHasPlayed()
    {
        var engine = CreateEngine();
        engine.CurrentPlayer.HasPlayed = true;

        var request = engine.BuildValidateRequest([new Tile(1, TileColor.Blue)]);

        request.HasPlayed.Should().BeTrue();
    }
}
