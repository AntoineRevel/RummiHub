using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Models;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

/// <summary>Games « Open to anyone »: listed in the lobby so strangers can share a table.</summary>
public class OpenGamesTests
{
    private static GameManager CreateManager(InMemoryGameJournal? journal = null) =>
        new(NullLogger<GameManager>.Instance, new InMemorySeedStore(), journal ?? new InMemoryGameJournal());

    [Fact]
    public void OnlyPublicGames_AreListed_WithTheirHostAndSeats()
    {
        var manager = CreateManager();
        manager.CreateGame("Alice", "conn-1");
        var open = manager.CreateGame("Bob", "conn-2", noJokers: true, isPublic: true);
        manager.AddAiPlayer(open.Id);

        manager.GetOpenGames().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            open.Id,
            Host = "Bob",
            Humans = 1,
            Ais = 1,
            NoJokers = true
        });
    }

    [Fact]
    public void AGame_LeavesTheList_WhenItStartsOrIsFull()
    {
        var manager = CreateManager();
        var started = manager.CreateGame("Alice", "conn-1", isPublic: true);
        manager.AddPlayer(started.Id, "Bob", "conn-2");
        var full = manager.CreateGame("Carol", "conn-3", isPublic: true);
        for (var i = 1; i < GameLimits.MaxPlayers; i++)
            manager.AddAiPlayer(full.Id);

        manager.GetOpenGames().Select(g => g.Id).Should().Equal(started.Id);

        started.IsStarted = true;
        manager.GetOpenGames().Should().BeEmpty();
    }

    [Fact]
    public void WhenTheHostLeaves_TheNextHumanBecomesHost()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1", isPublic: true);
        manager.AddAiPlayer(game.Id);
        manager.AddPlayer(game.Id, "Bob", "conn-2");

        manager.RemovePlayer("conn-1").Should().BeTrue();

        game.HostConnectionId.Should().Be("conn-2");
        game.GetLobbyPlayers().Single(p => p.IsHost).Name.Should().Be("Bob");
        manager.GetOpenGames().Single().Host.Should().Be("Bob");
    }

    [Fact]
    public void AGameWithOnlyAisLeft_IsRemoved()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1", isPublic: true);
        manager.AddAiPlayer(game.Id);

        manager.RemovePlayer("conn-1").Should().BeTrue();

        manager.GetGame(game.Id).Should().BeNull();
        manager.GetOpenGames().Should().BeEmpty();
    }

    [Fact]
    public void AGuestLeaving_LeavesTheHostInPlace()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1", isPublic: true);
        manager.AddPlayer(game.Id, "Bob", "conn-2");

        manager.RemovePlayer("conn-2").Should().BeTrue();

        game.HostConnectionId.Should().Be("conn-1");
        game.GetLobbyPlayers().Should().ContainSingle().Which.IsHost.Should().BeTrue();
    }

    [Fact]
    public void AnAiSeat_IsNeverMarkedHost()
    {
        var manager = CreateManager();
        var game = manager.CreateGame("Alice", "conn-1");
        manager.AddAiPlayer(game.Id);

        game.GetLobbyPlayers().Select(p => p.IsHost).Should().Equal(true, false);
    }

    [Fact]
    public void ThePublicOption_IsJournaled()
    {
        var journal = new InMemoryGameJournal();
        var manager = CreateManager(journal);
        manager.CreateGame("Alice", "conn-1", isPublic: true);
        manager.CreateGame("Bob", "conn-2");

        journal.Entries.Where(e => e.Kind == JournalKinds.Created).Select(e => e.Public)
            .Should().Equal(true, null);
    }
}
