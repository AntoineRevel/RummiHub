using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class GameManagerTests
{
    private static GameManager CreateManager() => CreateManager(new InMemorySeedStore());

    private static GameManager CreateManager(ISeedStore seedStore) =>
        new(NullLogger<GameManager>.Instance, seedStore);

    private static readonly string SeedA = new('a', DistributionSeeds.Length);
    private static readonly string SeedB = new('b', DistributionSeeds.Length);

    [Fact]
    public void AddAiPlayer_AddsAutoSeatWithUniqueName()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");

        var ai1 = manager.AddAiPlayer(game.Id);
        var ai2 = manager.AddAiPlayer(game.Id);

        ai1.Should().NotBeNull();
        ai2.Should().NotBeNull();
        ai1!.IsAuto.Should().BeTrue();
        ai2!.IsAuto.Should().BeTrue();
        ai2.Name.Should().NotBe(ai1.Name);
        game.GetPlayerNames().Should().BeEquivalentTo(["Alice", ai1.Name, ai2.Name]);
    }

    [Fact]
    public void AddAiPlayer_AiSeatHasNoConnection()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");

        var ai = manager.AddAiPlayer(game.Id);

        game.GetPlayers().Single(p => p.Name == ai!.Name).ConnectionId.Should().BeEmpty();
    }

    [Fact]
    public void AddAiPlayer_ReturnsNull_WhenGameMissing()
    {
        var manager = CreateManager();

        manager.AddAiPlayer("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void AddAiPlayer_ReturnsNull_WhenGameAlreadyStarted()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");
        game.IsStarted = true;

        manager.AddAiPlayer(game.Id).Should().BeNull();
    }

    [Fact]
    public void InitializeEngine_FlowsAutoFlagsToEngine()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");
        var ai = manager.AddAiPlayer(game.Id);

        manager.InitializeEngine(game.Id).Should().BeTrue();

        game.Engine!.Players.Single(p => p.Name == "Alice").IsAuto.Should().BeFalse();
        game.Engine.Players.Single(p => p.Name == ai!.Name).IsAuto.Should().BeTrue();
    }

    [Fact]
    public void CreateGame_WithAPastedSeed_AddsItToTheList()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1", "  " + SeedA.ToUpperInvariant() + " ");

        game.DistributionSeed.Should().Be(SeedA);
        game.SeedSaved.Should().BeTrue();
        manager.GetSeedList().Select(s => s.Seed).Should().Equal(SeedA);
    }

    [Fact]
    public void CreateGame_WithAListedSeed_LeavesTheListAsIs()
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA);
        store.Add(SeedB);
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1", SeedA);

        game.SeedSaved.Should().BeTrue();
        manager.GetSeedList().Select(s => s.Seed).Should().Equal(SeedB, SeedA);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("15e005a3")]
    [InlineData("not-a-seed")]
    public void CreateGame_RefusesAMalformedSeed_AndSaysTheExpectedLength(string seed)
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);

        var act = () => manager.CreateGame("Alice", "conn-1", seed);

        act.Should().Throw<ArgumentException>();
        store.Seeds.Should().BeEmpty();
    }

    [Fact]
    public void CreateGame_WithARandomSeed_DoesNotListIt()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1");

        game.SeedSaved.Should().BeFalse();
        store.Seeds.Should().BeEmpty();
    }

    [Fact]
    public void SaveSeed_DuringPlay_ListsItAtOnce()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);
        var game = manager.CreateGame("Alice", "conn-1");
        manager.AddAiPlayer(game.Id);
        game.IsStarted = true;
        manager.InitializeEngine(game.Id).Should().BeTrue();

        manager.SaveSeed(game);

        game.SeedSaved.Should().BeTrue();
        manager.GetSeedList().Select(s => s.Seed).Should().Equal(game.DistributionSeed);
    }

    [Fact]
    public void RemoveSeed_TakesTheSeedNamedByItsListIdOffTheList()
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA);
        store.Add(SeedB);
        var manager = CreateManager(store);

        manager.RemoveSeed(DistributionSeeds.ListId(SeedA));
        manager.RemoveSeed(SeedB);
        manager.RemoveSeed("unknown");

        manager.GetSeedList().Select(s => s.Seed).Should().Equal(SeedB);
    }

    [Fact]
    public void FindListedSeed_MapsAListIdBackToItsSeed_OnlyForListedSeeds()
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA);
        var manager = CreateManager(store);

        manager.FindListedSeed(DistributionSeeds.ListId(SeedA)).Should().Be(SeedA);
        manager.FindListedSeed(DistributionSeeds.ListId(SeedB)).Should().BeNull();
    }

    [Fact]
    public void ListIdAndShortForm_DoNotContainTheSeed()
    {
        var id = DistributionSeeds.ListId(SeedA);
        var other = DistributionSeeds.ListId(SeedB);

        id.Should().MatchRegex("^[0-9a-f]{16}$").And.NotBe(other);
        DistributionSeeds.Abbreviate(SeedA).Length.Should().BeLessThan(DistributionSeeds.Length / 4);
    }
}
