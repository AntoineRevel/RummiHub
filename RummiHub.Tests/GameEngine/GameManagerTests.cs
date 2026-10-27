using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class GameManagerTests
{
    private static GameManager CreateManager(params string[] predefinedIds)
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seeds-");
        var catalog = new FileSeedCatalog(
            Path.Combine(dir.FullName, "catalog.jsonl"),
            predefinedIds,
            NullLogger<FileSeedCatalog>.Instance);
        return new GameManager(NullLogger<GameManager>.Instance, catalog);
    }

    private static string Hex(char digit) => new(digit, DistributionSeeds.HexLength);

    [Fact]
    public async Task AddAiPlayer_AddsAutoSeatWithUniqueName()
    {
        var manager = CreateManager();
        var game = await manager.CreateGame("Alice", "conn-1");

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
    public async Task AddAiPlayer_AiSeatHasNoConnection()
    {
        var manager = CreateManager();
        var game = await manager.CreateGame("Alice", "conn-1");

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
    public async Task AddAiPlayer_ReturnsNull_WhenGameAlreadyStarted()
    {
        var manager = CreateManager();
        var game = await manager.CreateGame("Alice", "conn-1");
        game.IsStarted = true;

        manager.AddAiPlayer(game.Id).Should().BeNull();
    }

    [Fact]
    public async Task InitializeEngine_FlowsAutoFlagsToEngine()
    {
        var manager = CreateManager();
        var game = await manager.CreateGame("Alice", "conn-1");
        var ai = manager.AddAiPlayer(game.Id);

        manager.InitializeEngine(game.Id).Should().BeTrue();

        game.Engine!.Players.Single(p => p.Name == "Alice").IsAuto.Should().BeFalse();
        game.Engine.Players.Single(p => p.Name == ai!.Name).IsAuto.Should().BeTrue();
    }

    [Fact]
    public async Task AddSeed_MakesSeedAvailable_NewestFirst()
    {
        var manager = CreateManager();
        var older = Hex('a');
        var newer = Hex('b');

        (await manager.AddSeedAsync(older)).Status.Should().Be(SeedAddStatus.Added);
        (await manager.AddSeedAsync("  " + newer.ToUpperInvariant() + "  ")).Status.Should().Be(SeedAddStatus.Added);

        (await manager.GetProposedSeedsAsync()).Should().Equal(newer, older);
    }

    [Fact]
    public async Task AddSeed_IsIdempotent_AndDoesNotChangeOrder()
    {
        var manager = CreateManager();
        var first = Hex('c');
        var second = Hex('d');

        await manager.AddSeedAsync(first);
        await manager.AddSeedAsync(second);
        (await manager.AddSeedAsync(first)).Status.Should().Be(SeedAddStatus.AlreadyPresent);

        (await manager.GetProposedSeedsAsync()).Should().Equal(second, first);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("replay-me")]
    [InlineData("has space")]
    public async Task AddSeed_RejectsAnythingThatIsNot64Hex(string seed)
    {
        var manager = CreateManager();

        (await manager.AddSeedAsync(seed)).Status.Should().Be(SeedAddStatus.Invalid);
        (await manager.GetProposedSeedsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProposedSeeds_StartFromTheBootstrap_AndStayNewestFirst()
    {
        var manager = CreateManager("15e005a3", "76926024");
        var added = Hex('e');

        await manager.AddSeedAsync(added);

        (await manager.GetProposedSeedsAsync()).Should().Equal(added, "76926024", "15e005a3");
    }

    [Fact]
    public async Task SeatedPlayer_CanPublishTheDealtSeed_WhileTheGameIsInProgress()
    {
        var manager = CreateManager("15e005a3");
        var game = await manager.CreateGame("Alice", "conn");
        manager.AddPlayer(game.Id, "Bob", "conn-b");
        manager.InitializeEngine(game.Id).Should().BeTrue();

        game.RevealedDistributionSeed.Should().BeNull();
        var seed = SeedPublication.ForSeatedPlayer(game, "conn");
        seed.Should().Be(game.DistributionSeed);
        SeedPublication.ForSeatedPlayer(game, "stranger").Should().BeNull();

        (await manager.AddSeedAsync(seed!)).Status.Should().Be(SeedAddStatus.Added);
        (await manager.GetProposedSeedsAsync())[0].Should().Be(seed);
    }
}
