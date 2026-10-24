using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Models;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class PublicTableSecurityTests
{
    private sealed class RecordingSeedStore : ISeedStore
    {
        public List<string> Persisted { get; } = [];
        public void Persist(string seed) => Persisted.Add(seed);
    }

    private static GameManager CreateManager(params string[] predefinedIds) =>
        new(NullLogger<GameManager>.Instance,
            Options.Create(new GameOptions { PredefinedIds = predefinedIds.ToList() }),
            new RecordingSeedStore());

    private static GameManager CreateManager(ISeedStore store, params string[] predefinedIds) =>
        new(NullLogger<GameManager>.Instance,
            Options.Create(new GameOptions { PredefinedIds = predefinedIds.ToList() }),
            store);

    [Fact]
    public void CreateGame_DrawsACryptographicSeed_WhenTheClientDoesNotAsk()
    {
        var manager = CreateManager();

        var first = manager.CreateGame("Alice", "conn-1");
        var second = manager.CreateGame("Alice", "conn-2");

        first.DistributionSeed.Should().MatchRegex("^[0-9a-f]{64}$");
        second.DistributionSeed.Should().MatchRegex("^[0-9a-f]{64}$");
        second.DistributionSeed.Should().NotBe(first.DistributionSeed);
    }

    [Fact]
    public void CreateGame_RejectsAClientChosenSeed_AndDoesNotEchoIt()
    {
        var manager = CreateManager("15e005a3");

        var act = () => manager.CreateGame("Alice", "conn-1", "attacker-chosen-seed");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().NotContain("attacker-chosen-seed");
        manager.IsKnownDistributionSeed("attacker-chosen-seed").Should().BeFalse();
    }

    [Fact]
    public void CreateGame_AcceptsAPredefinedSeed_AndASeedSavedFromADealtGame()
    {
        var manager = CreateManager("15e005a3");

        manager.CreateGame("Alice", "conn-1", "15e005a3").DistributionSeed.Should().Be("15e005a3");

        manager.IsKnownDistributionSeed("replay-me").Should().BeFalse();
        manager.AddSeed("replay-me").Should().BeTrue();
        manager.CreateGame("Bob", "conn-2", "replay-me").DistributionSeed.Should().Be("replay-me");
    }

    [Fact]
    public void AddSeed_RejectsUnsafeTokens_AndDoesNotPersistThem()
    {
        var store = new RecordingSeedStore();
        var manager = CreateManager(store);

        manager.AddSeed(new string('a', DistributionSeeds.MaxLength + 1)).Should().BeFalse();
        manager.AddSeed("has space").Should().BeFalse();
        manager.AddSeed("../appsettings").Should().BeFalse();
        manager.AddSeed("ok_seed-1").Should().BeTrue();

        store.Persisted.Should().Equal("ok_seed-1");
        manager.GetAvailableGameIds().Should().BeEquivalentTo(["ok_seed-1"]);
    }

    [Fact]
    public void CreateGame_IssuesAnUnguessableSeatSecret_NotSharedAcrossSeatsOrGames()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-a");
        var bob = manager.AddPlayer(game.Id, "Bob", "conn-b");
        var other = manager.CreateGame("Alice", "conn-c");

        var alice = game.GetPlayers().Single(p => p.Name == "Alice");
        alice.SeatToken.Should().MatchRegex("^[0-9a-f]{64}$");
        alice.SeatToken.Should().NotBe(alice.Name).And.NotBe(game.Id).And.NotBe(alice.ConnectionId);
        bob!.SeatToken.Should().NotBe(alice.SeatToken);
        other.GetPlayers().Single().SeatToken.Should().NotBe(alice.SeatToken);

        typeof(Player).GetProperty(nameof(Player.SeatToken))!
            .GetCustomAttributes(typeof(JsonIgnoreAttribute), inherit: true)
            .Should().NotBeEmpty();
        typeof(GameStateDto).GetProperties().Select(p => p.Name)
            .Should().NotContain("SeatToken");
    }

    [Fact]
    public void UpdatePlayerConnection_RequiresTheSeatSecret()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");
        var alice = game.GetPlayers().Single();
        var bob = manager.AddPlayer(game.Id, "Bob", "conn-2")!;

        manager.UpdatePlayerConnection(game.Id, "Alice", "wrong-token", "stolen").Should().BeFalse();
        manager.UpdatePlayerConnection(game.Id, "Alice", bob.SeatToken, "stolen").Should().BeFalse();
        manager.UpdatePlayerConnection(game.Id, "Alice", null, "stolen").Should().BeFalse();
        game.FindPlayerIndexByConnection("conn-1").Should().Be(0);
        game.FindPlayerIndexByConnection("stolen").Should().BeNull();

        manager.UpdatePlayerConnection(game.Id, "alice", alice.SeatToken, "refreshed").Should().BeTrue();
        game.FindPlayerIndexByConnection("refreshed").Should().Be(0);
        game.FindPlayerIndexByConnection("conn-1").Should().BeNull();
    }

    [Fact]
    public void UpdatePlayerConnection_DoesNotAcceptAnotherGamesSecret_OrAnAiSeat()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");
        var ai = manager.AddAiPlayer(game.Id)!;
        var other = manager.CreateGame("Alice", "conn-2");
        var foreignToken = other.GetPlayers().Single().SeatToken;

        manager.UpdatePlayerConnection(game.Id, "Alice", foreignToken, "stolen").Should().BeFalse();
        game.FindPlayerIndexByConnection("conn-1").Should().Be(0);

        manager.UpdatePlayerConnection(game.Id, ai.Name, ai.SeatToken, "hijack").Should().BeFalse();
        game.GetPlayers().Single(p => p.IsAuto).ConnectionId.Should().BeEmpty();
    }

    [Fact]
    public void AddPlayer_EnforcesFourSeatsForHumansAndAi()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "c1");

        manager.AddPlayer(game.Id, "Bob", "c2").Should().NotBeNull();
        manager.AddPlayer(game.Id, "Cara", "c3").Should().NotBeNull();
        manager.AddAiPlayer(game.Id).Should().NotBeNull();
        manager.AddPlayer(game.Id, "Eve", "c5").Should().BeNull();
        manager.AddAiPlayer(game.Id).Should().BeNull();

        game.PlayerCount.Should().Be(GameLimits.MaxPlayers);
        game.GetPlayerNames().Should().HaveCount(GameLimits.MaxPlayers);
    }

    [Fact]
    public void InitializeEngine_RefusesASinglePlayer_AndDealsAFullTable()
    {
        var manager = CreateManager();
        var alone = manager.CreateGame("Alice", "c1");

        manager.InitializeEngine(alone.Id).Should().BeFalse();
        alone.Engine.Should().BeNull();

        var game = manager.CreateGame("Alice", "c1");
        manager.AddPlayer(game.Id, "Bob", "c2");
        manager.AddPlayer(game.Id, "Cara", "c3");
        manager.AddAiPlayer(game.Id);

        manager.InitializeEngine(game.Id).Should().BeTrue();
        game.Engine!.Players.Should().HaveCount(4);
        game.Engine.Players.Sum(p => p.Rack.Tiles.Count + p.Rack.Jokers).Should().Be(56);
    }

    [Fact]
    public void GeneratedSeed_ReplaysTheSameDeal()
    {
        var seed = DistributionSeeds.Create();
        DistributionSeeds.IsGenerated(seed).Should().BeTrue();

        var first = new GameEngine();
        var second = new GameEngine();
        first.Initialize(["Alice", "Bob", "Cara"], seed);
        second.Initialize(["Alice", "Bob", "Cara"], seed);

        DealOf(second).Should().Equal(DealOf(first));
    }

    [Fact]
    public void ConfiguredSeed_KeepsTheDealItWasSavedWith()
    {
        DistributionSeeds.IsGenerated("15e005a3").Should().BeFalse();

        var engine = new GameEngine();
        engine.Initialize(["Alice", "Bob"], "15e005a3");

        string.Join(", ", engine.Players[0].Rack.GetAllTilesIncludingJokers()).Should().Be(
            "Mango 12, Blue 13, Red 9, Blue 13, Red 8, Black 2, Blue 12, Mango 12, Mango 3, " +
            "Black 6, Black 9, Mango 4, Black 11, Red 9");
        string.Join(", ", Enumerable.Range(0, 5).Select(_ => engine.DrawTile())).Should().Be(
            "Mango 5, Red 10, Black 8, Mango 10, Black 4");
    }

    [Theory]
    [InlineData("15e005a3")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void IsGenerated_RejectsOtherShapes(string seed)
    {
        DistributionSeeds.IsGenerated(seed).Should().BeFalse();
    }

    [Fact]
    public void Seed_StaysHiddenUntilTheGameIsOver()
    {
        var seed = DistributionSeeds.Create();
        var engine = new GameEngine();
        engine.Initialize(["Alice", "Bob"], seed);
        var game = new Game { DistributionSeed = seed, Engine = engine };

        game.RevealedDistributionSeed.Should().BeNull();

        // Alice goes out with her last three tiles.
        var lastTiles = new List<Tile> { new(11, TileColor.Blue), new(11, TileColor.Red), new(11, TileColor.Black) };
        engine.CurrentPlayer.Rack = new Set(lastTiles);
        var board = new Solution { IsValid = true };
        board.Sets.Add(new ValidSet(lastTiles.ToArray()));
        engine.ApplySolveResult(new SolveResponse
        {
            RequestId = "last-move",
            Found = true,
            Score = 33,
            NewBoard = board,
            TilesToPlay = lastTiles
        });

        engine.IsGameOver.Should().BeTrue();
        game.RevealedDistributionSeed.Should().Be(seed);
    }

    [Fact]
    public void PlayAttempts_AreThrottledPerSeat()
    {
        var game = new Game();
        var start = DateTimeOffset.UnixEpoch;

        for (var i = 0; i < GameLimits.PlayAttemptsPerWindow; i++)
            game.TryStartPlayAttempt(0, start.AddMilliseconds(i)).Should().BeTrue();

        game.TryStartPlayAttempt(0, start.AddSeconds(1)).Should().BeFalse();
        // Another seat at the same table keeps its own budget.
        game.TryStartPlayAttempt(1, start.AddSeconds(1)).Should().BeTrue();
        // Once the window has passed, the first seat may try again.
        game.TryStartPlayAttempt(0, start + GameLimits.PlayAttemptWindow).Should().BeTrue();
    }

    private static List<string> DealOf(GameEngine engine)
    {
        var deal = engine.Players
            .Select(p => string.Join(",", p.Rack.GetAllTilesIncludingJokers()))
            .ToList();
        while (engine.TilePoolCount > 0)
            deal.Add(engine.DrawTile().ToString());
        return deal;
    }
}
