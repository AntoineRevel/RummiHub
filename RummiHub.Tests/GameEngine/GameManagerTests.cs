using FluentAssertions;
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
    public void CreateGame_WithAPastedSeed_AddsItToTheList_HiddenUntilTheGameEnds()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1", "  " + SeedA.ToUpperInvariant() + " ");

        game.DistributionSeed.Should().Be(SeedA);
        game.SeedSaved.Should().BeTrue();
        store.Seeds.Should().ContainSingle(s => s.Seed == SeedA && s.HiddenUntil != null);
        manager.GetSeedList().Should().BeEmpty();

        manager.RevealSeed(game);

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

    [Fact]
    public void CreateGame_AcceptsALegacySeed_WhenItIsNotALiveGameCode()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1", "15E005A3");

        game.DistributionSeed.Should().Be("15e005a3");
        manager.GetSeedList().Should().BeEmpty();
    }

    [Fact]
    public void CreateGame_ExplainsWhenAnEightCharacterValueIsALiveGameCode()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);
        var live = manager.CreateGame("Alice", "conn-1");

        var act = () => manager.CreateGame("Bob", "conn-2", live.Id.Insert(4, "-").ToUpperInvariant());

        act.Should().Throw<ArgumentException>()
            .WithMessage(DistributionSeeds.GameCodePastedMessage(live.Id));
        store.Seeds.Should().BeEmpty();
        manager.GetGame(live.Id).Should().NotBeNull();
    }

    [Theory]
    [InlineData("abc")]
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
    public void SaveSeed_DuringPlay_HidesItFromTheLobby_UntilTheGameIsRemoved()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);
        var game = manager.CreateGame("Alice", "conn-1");
        manager.AddAiPlayer(game.Id);
        game.IsStarted = true;
        manager.InitializeEngine(game.Id).Should().BeTrue();

        manager.SaveSeed(game);

        game.SeedSaved.Should().BeTrue();
        store.Seeds.Should().ContainSingle(s => s.Seed == game.DistributionSeed && s.HiddenUntil != null);
        manager.GetSeedList().Should().BeEmpty();

        manager.RevealSeed(game);

        manager.GetSeedList().Select(s => s.Seed).Should().Equal(game.DistributionSeed);
    }

    [Fact]
    public void GetSeedList_ShowsAHiddenSeedOnceItsDelayHasPassed()
    {
        var store = new InMemorySeedStore();
        store.Seeds.Add(new SavedSeed(SeedA, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddMinutes(-1)));
        store.Seeds.Add(new SavedSeed(SeedB, DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddHours(1)));

        CreateManager(store).GetSeedList().Select(s => s.Seed).Should().Equal(SeedA);
    }
}
