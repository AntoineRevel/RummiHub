using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class GameManagerTests
{
    private sealed class RecordingSeedStore : ISeedStore
    {
        public List<string> Persisted { get; } = [];
        public void Persist(string seed) => Persisted.Add(seed);
    }

    private static GameManager CreateManager() =>
        new(NullLogger<GameManager>.Instance, Options.Create(new GameOptions()), new RecordingSeedStore());

    private static GameManager CreateManager(params string[] predefinedIds) =>
        new(NullLogger<GameManager>.Instance,
            Options.Create(new GameOptions { PredefinedIds = predefinedIds.ToList() }), new RecordingSeedStore());

    private static GameManager CreateManager(ISeedStore seedStore) =>
        new(NullLogger<GameManager>.Instance, Options.Create(new GameOptions()), seedStore);

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
    public void AddSeed_MakesSeedAvailableInLobbyList()
    {
        var manager = CreateManager();

        manager.AddSeed("replay-me").Should().BeTrue();

        manager.GetAvailableGameIds().Should().Contain("replay-me");
    }

    [Fact]
    public void AddSeed_IsIdempotent_AndListedOnce()
    {
        var manager = CreateManager();

        manager.AddSeed("dup").Should().BeTrue();
        manager.AddSeed("dup").Should().BeFalse();

        manager.GetAvailableGameIds().Where(id => id == "dup").Should().HaveCount(1);
    }

    [Fact]
    public void AddSeed_TrimsWhitespace()
    {
        var manager = CreateManager();

        manager.AddSeed("  spaced  ").Should().BeTrue();

        manager.GetAvailableGameIds().Should().Contain("spaced");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AddSeed_ReturnsFalse_WhenBlank(string seed)
    {
        var manager = CreateManager();

        manager.AddSeed(seed).Should().BeFalse();
        manager.GetAvailableGameIds().Should().BeEmpty();
    }

    [Fact]
    public void GetAvailableGameIds_UnionsPredefinedAndSavedSeeds()
    {
        var manager = CreateManager("predef-1", "predef-2");

        manager.AddSeed("saved-1");

        manager.GetAvailableGameIds().Should().BeEquivalentTo(["predef-1", "predef-2", "saved-1"]);
    }

    [Fact]
    public void AddSeed_PersistsSeed_WhenNewlyAdded()
    {
        var store = new RecordingSeedStore();
        var manager = CreateManager(store);

        manager.AddSeed("  spaced  ").Should().BeTrue();

        store.Persisted.Should().Equal("spaced");
    }

    [Fact]
    public void AddSeed_DoesNotPersist_WhenDuplicate()
    {
        var store = new RecordingSeedStore();
        var manager = CreateManager(store);

        manager.AddSeed("dup").Should().BeTrue();
        manager.AddSeed("dup").Should().BeFalse();

        store.Persisted.Should().Equal("dup");
    }
}
