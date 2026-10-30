using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Models;
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
    public void CreateGame_WithAPastedSeed_DealsItWithoutListingIt()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1", "  " + SeedA.ToUpperInvariant() + " ");

        game.DistributionSeed.Should().Be(SeedA);
        game.SavedFrom.Should().BeNull();
        store.Seeds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(4, 3)]
    public void CreateGame_FromAListEntry_SeatsAIsUpToItsSize_WithTheHostOnItsSeat(int players, int seat)
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA, players, seat, "Replay");
        var manager = CreateManager(store);

        var game = manager.CreateGame("Alice", "conn-1", store.Seeds.Single());

        game.DistributionSeed.Should().Be(SeedA);
        var seats = game.GetLobbyPlayers();
        seats.Should().HaveCount(players);
        seats[seat].Should().Be(new LobbyPlayerDto("Alice", false));
        seats.Where((_, i) => i != seat).Should().OnlyContain(p => p.IsAuto);
    }

    [Fact]
    public void AHostReplayingAnEntry_GetsTheRackOfTheSeatItWasSavedFrom()
    {
        var original = CreateManager();
        var first = original.CreateGame("Bob", "conn-0", SeedA);
        original.AddAiPlayer(first.Id);
        original.AddAiPlayer(first.Id);
        original.InitializeEngine(first.Id).Should().BeTrue();
        var savedRack = first.Engine!.Players[2].Rack.GetAllTilesIncludingJokers();

        var store = new InMemorySeedStore();
        store.Add(SeedA, 3, 2, null);
        var manager = CreateManager(store);
        var game = manager.CreateGame("Alice", "conn-1", store.Seeds.Single());
        manager.InitializeEngine(game.Id).Should().BeTrue();

        var alice = game.Engine!.Players.Single(p => p.Name == "Alice");
        alice.Rack.GetAllTilesIncludingJokers().Should().Equal(savedRack);
        game.Engine.TilePoolCount.Should().Be(first.Engine.TilePoolCount);
        game.IsSeedSaved(2).Should().BeTrue();
    }

    [Fact]
    public void TheHostKeepsTheSavedSeat_WhenAnAiMakesRoomForAFriend()
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA, 3, 1, null);
        var manager = CreateManager(store);
        var game = manager.CreateGame("Alice", "conn-1", store.Seeds.Single());
        var firstAi = game.GetLobbyPlayers()[0].Name;

        manager.RemoveAiPlayer(game.Id, firstAi).Should().NotBeNull();
        game.GetPlayerNames()[1].Should().Be("Alice");
        manager.AddPlayer(game.Id, "Bob", "conn-2").Should().NotBeNull();

        game.GetPlayerNames().Should().HaveCount(3);
        game.GetPlayerNames()[1].Should().Be("Alice");
    }

    [Fact]
    public void RemoveAiPlayer_TakesOnlyAnAiSeat_BeforeTheStart()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");
        manager.AddPlayer(game.Id, "Bob", "conn-2");
        var ai = manager.AddAiPlayer(game.Id)!;

        manager.RemoveAiPlayer(game.Id, "Bob").Should().BeNull();
        manager.RemoveAiPlayer(game.Id, "nobody").Should().BeNull();
        manager.RemoveAiPlayer(game.Id, ai.Name).Should().NotBeNull();
        game.GetPlayerNames().Should().Equal("Alice", "Bob");

        var later = manager.AddAiPlayer(game.Id)!;
        game.IsStarted = true;
        manager.RemoveAiPlayer(game.Id, later.Name).Should().BeNull();
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

        game.GetSavedSeats().Should().BeEmpty();
        store.Seeds.Should().BeEmpty();
    }

    [Fact]
    public void SaveSeed_DuringPlay_ListsTheTableSizeAndSeat_UnderItsName()
    {
        var store = new InMemorySeedStore();
        var manager = CreateManager(store);
        var game = manager.CreateGame("Alice", "conn-1");
        manager.AddAiPlayer(game.Id);
        manager.AddAiPlayer(game.Id);
        game.IsStarted = true;
        manager.InitializeEngine(game.Id).Should().BeTrue();

        manager.SaveSeed(game, 1, " Good one ");

        game.GetSavedSeats().Should().Equal(1);
        var saved = manager.GetSeedList().Single();
        (saved.Seed, saved.Players, saved.Seat, saved.Name).Should().Be((game.DistributionSeed!, 3, 1, "Good one"));
    }

    [Fact]
    public void RemoveSeed_AndRenameSeed_ActOnTheEntryNamedByItsListId()
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA, 2, 0, null);
        store.Add(SeedB, 2, 0, null);
        var manager = CreateManager(store);

        manager.RenameSeed(DistributionSeeds.ListId(SeedB, 2, 0), "Bee");
        manager.RemoveSeed(DistributionSeeds.ListId(SeedA, 2, 0));
        manager.RemoveSeed(SeedB);
        manager.RemoveSeed("unknown");

        manager.GetSeedList().Select(s => (s.Seed, s.Name)).Should().Equal((SeedB, "Bee"));
    }

    [Fact]
    public void FindListedSeed_MapsAListIdBackToItsEntry_OnlyForListedOnes()
    {
        var store = new InMemorySeedStore();
        store.Add(SeedA, 3, 2, null);
        var manager = CreateManager(store);

        manager.FindListedSeed(DistributionSeeds.ListId(SeedA, 3, 2))!.Seed.Should().Be(SeedA);
        manager.FindListedSeed(DistributionSeeds.ListId(SeedA, 3, 1)).Should().BeNull();
    }

    [Fact]
    public void ListIdAndShortForm_DoNotContainTheSeed()
    {
        var id = DistributionSeeds.ListId(SeedA, 2, 0);
        var other = DistributionSeeds.ListId(SeedB, 2, 0);

        id.Should().MatchRegex("^[0-9a-f]{16}$").And.NotBe(other);
        DistributionSeeds.Abbreviate(SeedA).Length.Should().BeLessThan(DistributionSeeds.Length / 4);
    }
}
