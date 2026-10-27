using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Models;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class PublicTableSecurityTests
{
    private static GameManager CreateManager() =>
        new(NullLogger<GameManager>.Instance, new InMemorySeedStore());

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

    [Theory]
    [InlineData("../appsettings")]
    [InlineData("has space but not hex")]
    [InlineData("{\"GameOptions\":null}")]
    public void CreateGame_RefusesASeedThatIsNotHex(string seed)
    {
        var manager = CreateManager();

        var act = () => manager.CreateGame("Alice", "conn-1", seed);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void CreateGame_RefusesAnOversizedSeed()
    {
        var manager = CreateManager();

        var act = () => manager.CreateGame("Alice", "conn-1", new string('a', 10_000));

        act.Should().Throw<ArgumentException>();
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
        DistributionSeeds.IsValid(seed).Should().BeTrue();

        var first = new GameEngine();
        var second = new GameEngine();
        first.Initialize(["Alice", "Bob", "Cara"], seed);
        second.Initialize(["Alice", "Bob", "Cara"], seed);

        DealOf(second).Should().Equal(DealOf(first));
    }

    [Fact]
    public void ASavedSeed_AlwaysDealsTheSameTiles()
    {
        // Pinned: changing the shuffle would change the game every saved seed replays.
        var engine = new GameEngine();
        engine.Initialize(["Alice", "Bob"], TestSeeds.Of("pinned"));

        string.Join(", ", engine.Players[0].Rack.GetAllTilesIncludingJokers()).Should().Be(
            "Red 8, Black 8, Mango 6, Blue 1, Red 2, Black 7, Mango 10, Blue 4, Blue 8, Blue 12, " +
            "Mango 2, Red 6, Mango 1, Blue 1");
        string.Join(", ", Enumerable.Range(0, 5).Select(_ => engine.DrawTile())).Should().Be(
            "Blue 7, Blue 3, Blue 10, Red 13, Blue 9");
    }

    [Theory]
    [InlineData("15e005a3")]
    [InlineData("audit-seed")]
    public void Initialize_RefusesASeedThatIsNot64Hex(string seed)
    {
        var engine = new GameEngine();

        var act = () => engine.Initialize(["Alice", "Bob"], seed);

        act.Should().Throw<ArgumentException>();
        engine.Players.Should().BeEmpty();
    }

    [Theory]
    [InlineData("15e005a3")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void IsValid_RejectsOtherShapes(string seed)
    {
        DistributionSeeds.IsValid(seed).Should().BeFalse();
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
