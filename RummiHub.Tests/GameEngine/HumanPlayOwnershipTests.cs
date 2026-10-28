using System.Net;
using AwesomeAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

/// <summary>
/// A client must not be able to play tiles it does not hold. The check is local:
/// no Redis, no solver. The audit repro is seed "audit-seed", Alice to play.
/// </summary>
public class HumanPlayOwnershipTests
{
    private static readonly List<Tile> ForeignThirteens =
    [
        new(13, TileColor.Blue),
        new(13, TileColor.Red),
        new(13, TileColor.Black)
    ];

    [Fact]
    public async Task ForeignTiles_AreRejectedBeforeSolving_AndStateStaysAt106()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        alice.HasPlayed = true;

        alice.Name.Should().Be("Alice");
        RackCount(alice).Should().Be(14);
        engine.TilePoolCount.Should().Be(78);
        TotalTiles(engine).Should().Be(106);
        alice.Rack.ContainsAll(ForeignThirteens).Should().BeFalse();

        var rackBefore = alice.Rack.GetAllTilesIncludingJokers().ToList();
        var probe = new ProbeHandler();
        using var client = new HttpClient(probe) { BaseAddress = new Uri("http://solver.test") };

        engine.SelectionOwnershipError(ForeignThirteens)
            .Should().Be(GameEngine.UnownedSelectionMessage);

        var response = await engine.ValidateHumanPlay(
            ForeignThirteens, client, null!, CancellationToken.None);

        response.Found.Should().BeFalse();
        response.Error.Should().Be(GameEngine.UnownedSelectionMessage);
        probe.Calls.Should().Be(0);
        alice.Rack.GetAllTilesIncludingJokers().Should().Equal(rackBefore);
        alice.HasPlayed.Should().BeTrue();
        engine.Board.Sets.Should().BeEmpty();
        engine.TilePoolCount.Should().Be(78);
        TotalTiles(engine).Should().Be(106);
    }

    [Fact]
    public void ApplySolveResult_ForeignTiles_DoesNotCreateTiles()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        alice.HasPlayed = true;
        var rackBefore = alice.Rack.GetAllTilesIncludingJokers().ToList();

        // The solver accepts this group (score 39) when the selection is presented as the hand.
        // Applying it used to leave the rack at 14 and the pool at 78 while the board grew,
        // for 109 tiles in the game.
        var act = () => engine.ApplySolveResult(Accepted(ForeignThirteens, score: 39));

        act.Should().Throw<InvalidOperationException>();
        alice.Rack.GetAllTilesIncludingJokers().Should().Equal(rackBefore);
        alice.HasPlayed.Should().BeTrue();
        alice.TilesToPlay.Should().BeEmpty();
        engine.Board.Sets.Should().BeEmpty();
        engine.TilePoolCount.Should().Be(78);
        engine.IsGameOver.Should().BeFalse();
        TotalTiles(engine).Should().Be(106);
    }

    [Fact]
    public void ExtraDuplicateCopy_IsRejected()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        var held = new Tile(7, TileColor.Black);
        var other = new Tile(1, TileColor.Blue);
        alice.Rack = new Set([held, other]);

        var selection = new List<Tile> { held, held };

        engine.SelectionOwnershipError(selection).Should().Be(GameEngine.UnownedSelectionMessage);

        var act = () => engine.ApplySolveResult(Accepted(selection));

        act.Should().Throw<InvalidOperationException>();
        alice.Rack.Tiles.Should().BeEquivalentTo([held, other]);
        alice.Rack.Jokers.Should().Be(0);
        alice.HasPlayed.Should().BeFalse();
        engine.Board.Sets.Should().BeEmpty();
    }

    [Fact]
    public void BothCopies_WhenHeld_AreAccepted()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        var held = new Tile(7, TileColor.Black);
        var other = new Tile(1, TileColor.Blue);
        alice.Rack = new Set([held, held, other]);
        var before = TotalTiles(engine);

        engine.SelectionOwnershipError([held, held]).Should().BeNull();

        engine.ApplySolveResult(Accepted([held, held]));

        alice.HasPlayed.Should().BeTrue();
        alice.Rack.Tiles.Should().BeEquivalentTo([other]);
        TotalTiles(engine).Should().Be(before);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void ExtraJoker_IsRejected(int heldJokers, int requestedJokers)
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        var five = new Tile(5, TileColor.Red);
        var tiles = new List<Tile> { five };
        for (var i = 0; i < heldJokers; i++)
            tiles.Add(new Tile(isJoker: true));
        alice.Rack = new Set(tiles);

        // A joker that still carries a value counts as a joker, not as that numbered tile.
        var selection = new List<Tile>();
        for (var i = 0; i < requestedJokers; i++)
            selection.Add(new Tile(13, TileColor.Blue, isJoker: true));

        engine.SelectionOwnershipError(selection).Should().Be(GameEngine.UnownedSelectionMessage);

        var act = () => engine.ApplySolveResult(new SolveResponse
        {
            RequestId = "jokers",
            Found = true,
            Score = 0,
            NewBoard = new Solution { IsValid = true },
            TilesToPlay = [],
            JokersToPlay = requestedJokers
        });

        act.Should().Throw<InvalidOperationException>();
        alice.Rack.Jokers.Should().Be(heldJokers);
        alice.Rack.Tiles.Should().ContainSingle().Which.Should().Be(five);
        alice.HasPlayed.Should().BeFalse();
        engine.Board.Sets.Should().BeEmpty();
    }

    [Fact]
    public void HeldJoker_IsAccepted()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        var five = new Tile(5, TileColor.Red);
        alice.Rack = new Set([five, new Tile(isJoker: true)]);
        var before = TotalTiles(engine);

        engine.SelectionOwnershipError([new Tile(13, TileColor.Blue, isJoker: true)]).Should().BeNull();

        engine.ApplySolveResult(new SolveResponse
        {
            RequestId = "joker",
            Found = true,
            Score = 13,
            NewBoard = GroupOf(new Tile(isJoker: true)),
            TilesToPlay = [],
            JokersToPlay = 1
        });

        alice.HasPlayed.Should().BeTrue();
        alice.Rack.Jokers.Should().Be(0);
        alice.Rack.Tiles.Should().ContainSingle().Which.Should().Be(five);
        engine.Board.ToSet().Jokers.Should().Be(1);
        TotalTiles(engine).Should().Be(before);
    }

    [Fact]
    public async Task OwnedSelection_IsAccepted_AndApplyConservesTiles()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        alice.HasPlayed.Should().BeFalse();
        var played = alice.Rack.GetAllTilesIncludingJokers().Take(3).ToList();
        var rackBefore = RackCount(alice);

        engine.SelectionOwnershipError(played).Should().BeNull();

        var probe = new ProbeHandler();
        using var client = new HttpClient(probe) { BaseAddress = new Uri("http://solver.test") };

        // Ownership is not a local "the move is legal" verdict: a held selection is still
        // forwarded to the solver. The probe fails the HTTP call before any state change.
        var act = () => engine.ValidateHumanPlay(played, client, null!, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();
        probe.Calls.Should().Be(1);
        alice.HasPlayed.Should().BeFalse();
        RackCount(alice).Should().Be(rackBefore);

        engine.ApplySolveResult(Accepted(played, score: 30));

        alice.HasPlayed.Should().BeTrue();
        RackCount(alice).Should().Be(rackBefore - played.Count);
        engine.TilePoolCount.Should().Be(78);
        engine.Board.ToSet().GetAllTilesIncludingJokers().Should().BeEquivalentTo(played);
        TotalTiles(engine).Should().Be(106);
    }

    [Fact]
    public async Task ForeignTiles_CannotBypassInitialMeld()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        alice.HasPlayed.Should().BeFalse();
        var rackBefore = alice.Rack.GetAllTilesIncludingJokers().ToList();

        var probe = new ProbeHandler();
        using var client = new HttpClient(probe) { BaseAddress = new Uri("http://solver.test") };

        var response = await engine.ValidateHumanPlay(
            ForeignThirteens, client, null!, CancellationToken.None);

        response.Found.Should().BeFalse();
        response.Error.Should().Be(GameEngine.UnownedSelectionMessage);
        probe.Calls.Should().Be(0);

        var act = () => engine.ApplySolveResult(Accepted(ForeignThirteens, score: 39));

        act.Should().Throw<InvalidOperationException>();
        alice.HasPlayed.Should().BeFalse();
        alice.TilesToPlay.Should().BeEmpty();
        alice.Rack.GetAllTilesIncludingJokers().Should().Equal(rackBefore);
        engine.Board.Sets.Should().BeEmpty();
        engine.TilePoolCount.Should().Be(78);
        engine.IsGameOver.Should().BeFalse();
        engine.WinnerName.Should().BeNull();
        TotalTiles(engine).Should().Be(106);
    }

    [Fact]
    public void NullSelection_IsRejectedWithoutTouchingState()
    {
        var engine = CreateAuditGame();
        var alice = engine.CurrentPlayer;
        var rackBefore = alice.Rack.GetAllTilesIncludingJokers().ToList();

        engine.SelectionOwnershipError(null).Should().Be(GameEngine.UnownedSelectionMessage);

        alice.Rack.GetAllTilesIncludingJokers().Should().Equal(rackBefore);
        alice.HasPlayed.Should().BeFalse();
        TotalTiles(engine).Should().Be(106);
    }

    private static GameEngine CreateAuditGame()
    {
        var engine = new GameEngine();
        engine.Initialize(["Alice", "Bob"], TestSeeds.Of("audit-seed"));
        return engine;
    }

    private static int RackCount(GamePlayer player) => player.Rack.Tiles.Count + player.Rack.Jokers;

    private static int TotalTiles(GameEngine engine)
    {
        var racks = engine.Players.Sum(p => p.Rack.Tiles.Count + p.Rack.Jokers);
        var board = engine.Board.ToSet();
        return racks + board.Tiles.Count + board.Jokers + engine.TilePoolCount;
    }

    private static SolveResponse Accepted(List<Tile> tiles, int score = 0)
    {
        var colored = tiles.Where(t => !t.IsJoker).ToList();
        var jokers = tiles.Count(t => t.IsJoker);
        return new SolveResponse
        {
            RequestId = "audit",
            Found = true,
            Score = score,
            NewBoard = GroupOf(tiles.ToArray()),
            TilesToPlay = colored,
            JokersToPlay = jokers
        };
    }

    private static Solution GroupOf(params Tile[] tiles)
    {
        var board = new Solution { IsValid = true };
        if (tiles.Length > 0)
            board.Sets.Add(new ValidSet(tiles));
        return board;
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
