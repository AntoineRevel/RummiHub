using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Tests.GameEngineTests;
using RummiHub.Web.Services;

namespace RummiHub.Tests.Web;

public class GameJournalTests
{
    private const string Alice = "alice-browser-1";
    private const string Bob = "bob-browser-22";

    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public void Advance(TimeSpan by) => now += by;
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---------- File ----------

    [Fact]
    public void FileJournal_ReadsBackWhatItWrote_DatedByItsClock_AndSkipsABrokenLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rummihub-journal-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "journal.jsonl");
        try
        {
            var clock = new ManualClock(T0);
            var journal = new FileGameJournal(path, clock, NullLogger<FileGameJournal>.Instance);

            journal.Record(new JournalEntry { Kind = JournalKinds.Created, Game = "g1", Visitor = Alice, Name = "Alice", Seed = "random" });
            File.AppendAllText(path, "{\"kind\":\"played\",\"ga\n");
            clock.Advance(TimeSpan.FromMinutes(1));
            journal.Record(new JournalEntry
            {
                Kind = JournalKinds.Started, Game = "g1",
                Seats = [new JournalSeat("Alice", false, Alice), new JournalSeat("AI 1", true, null)]
            });

            var entries = journal.ReadAll();

            entries.Should().HaveCount(2);
            entries[0].At.Should().Be(T0);
            entries[0].Visitor.Should().Be(Alice);
            entries[0].Seed.Should().Be("random");
            entries[1].At.Should().Be(T0.AddMinutes(1));
            entries[1].Seats.Should().Equal(new JournalSeat("Alice", false, Alice), new JournalSeat("AI 1", true, null));
            File.ReadAllLines(path)[0].Should().NotContain("seats", "a field the entry does not use stays out of the file");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FileJournal_WithoutAFile_IsEmpty() =>
        new FileGameJournal(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "none.jsonl"),
                TimeProvider.System, NullLogger<FileGameJournal>.Instance)
            .ReadAll().Should().BeEmpty();

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef")]
    [InlineData("  abc-DEF_123  ", "abc-DEF_123")]
    [InlineData("short", null)]
    [InlineData("has space inside", null)]
    [InlineData("<script>alert(1)</script>", null)]
    [InlineData(null, null)]
    public void VisitorIds_KeepOnlyTheShapeStatsJsMakes(string? id, string? expected) =>
        VisitorIds.Normalize(id).Should().Be(expected);

    // ---------- What the game manager records ----------

    [Fact]
    public void GameManager_RecordsCreationJoinAndStart_WithEachHumansVisitorId()
    {
        var journal = new InMemoryGameJournal();
        var manager = new GameManager(NullLogger<GameManager>.Instance, new InMemorySeedStore(), journal);

        var game = manager.CreateGame("Alice", "c1", visitorId: Alice);
        manager.AddPlayer(game.Id, "Bob", "c2", "not valid!");
        manager.AddAiPlayer(game.Id);
        manager.InitializeEngine(game.Id).Should().BeTrue();

        journal.Entries.Select(e => e.Kind).Should().Equal(
            JournalKinds.Created, JournalKinds.Joined, JournalKinds.Started);
        journal.Entries[0].Should().BeEquivalentTo(new
        {
            Game = game.Id, Visitor = Alice, Name = "Alice", Seed = "random"
        });
        journal.Entries[1].Visitor.Should().BeNull("a malformed id is dropped, not stored");
        journal.Entries[2].Seats.Should().Equal(
            new JournalSeat("Alice", false, Alice),
            new JournalSeat("Bob", false, null),
            new JournalSeat("AI 1", true, null));
    }

    [Fact]
    public void GameManager_TellsAPastedSeedFromARandomOne()
    {
        var journal = new InMemoryGameJournal();
        var manager = new GameManager(NullLogger<GameManager>.Instance, new InMemorySeedStore(), journal);

        manager.CreateGame("Alice", "c1", new string('a', DistributionSeeds.Length));

        journal.Entries.Single().Seed.Should().Be("pasted");
    }

    // ---------- Statistics ----------

    private static JournalEntry At(int minutes, JournalEntry e) => e with { At = T0.AddMinutes(minutes) };

    private static JournalEntry Created(string game, string name, string? visitor) =>
        new() { Kind = JournalKinds.Created, Game = game, Name = name, Visitor = visitor, Seed = "random" };

    private static JournalEntry Started(string game, params JournalSeat[] seats) =>
        new() { Kind = JournalKinds.Started, Game = game, Seats = [..seats] };

    private static JournalEntry Move(string game, int seat, int tiles) =>
        new() { Kind = tiles > 0 ? JournalKinds.Played : JournalKinds.Drew, Game = game, Seat = seat, Tiles = tiles > 0 ? tiles : null };

    private static JournalEntry Ended(string game, int winner, int turns) =>
        new() { Kind = JournalKinds.Ended, Game = game, Seat = winner, Turns = turns, TilesLeft = [0, 5] };

    /// <summary>
    /// Alice plays an AI and wins; Alice and Bob play together and Bob leaves mid-game; Bob
    /// opens a game he never starts.
    /// </summary>
    private static List<JournalEntry> ThreeGames() =>
    [
        At(0, Created("solo", "Alice", Alice)),
        At(1, Started("solo", new("Alice", false, Alice), new("AI 1", true, null))),
        At(2, Move("solo", 0, 6)),
        At(3, Move("solo", 1, 0)),
        At(4, Move("solo", 0, 3)),
        At(4, Ended("solo", 0, 1)),

        At(10, Created("duo", "Alice", Alice)),
        At(11, new JournalEntry { Kind = JournalKinds.Joined, Game = "duo", Name = "Bob", Visitor = Bob }),
        At(12, Started("duo", new("Alice", false, Alice), new("Bob", false, Bob))),
        At(13, Move("duo", 0, 0)),
        At(14, Move("duo", 1, 4)),

        At(20, Created("lone", "Bobby", Bob))
    ];

    [Fact]
    public void Build_TellsEachGamesOutcome_AndHowManyHumansSatAtIt()
    {
        var report = GameStatistics.Build(ThreeGames(), T0.AddHours(3));

        report.Games.Select(g => g.Id).Should().Equal("lone", "duo", "solo");
        var solo = report.Games.Single(g => g.Id == "solo");
        var duo = report.Games.Single(g => g.Id == "duo");
        var lone = report.Games.Single(g => g.Id == "lone");

        solo.Outcome.Should().Be(GameOutcome.Finished);
        solo.Humans.Should().Be(1);
        solo.Ais.Should().Be(1);
        solo.Winner!.Name.Should().Be("Alice");
        solo.Duration.Should().Be(TimeSpan.FromMinutes(3));
        solo.Moves.Should().Be(3);
        solo.Seats[0].TilesPlayed.Should().Be(9);
        solo.Seats[1].Draws.Should().Be(1);

        duo.Outcome.Should().Be(GameOutcome.Abandoned);
        duo.Humans.Should().Be(2);
        lone.Outcome.Should().Be(GameOutcome.NeverStarted);
        report.Since.Should().Be(T0);
    }

    [Fact]
    public void Build_KeepsRecentGamesInProgressOrInTheLobby()
    {
        var report = GameStatistics.Build(ThreeGames(), T0.AddMinutes(30));

        report.Games.Single(g => g.Id == "duo").Outcome.Should().Be(GameOutcome.InProgress);
        report.Games.Single(g => g.Id == "lone").Outcome.Should().Be(GameOutcome.Lobby);
    }

    [Fact]
    public void Build_CountsEachPlayerByVisitorId_AcrossNamesAndGames()
    {
        var report = GameStatistics.Build(ThreeGames(), T0.AddHours(3));

        report.Players.Should().HaveCount(2);
        var alice = report.Players.Single(p => p.Key == Alice);
        var bob = report.Players.Single(p => p.Key == Bob);

        alice.Should().BeEquivalentTo(new
        {
            Created = 2, Joined = 0, Played = 2, WithOtherHumans = 1, Finished = 1, Won = 1, Abandoned = 1,
            TilesPlayed = 9, Draws = 1
        });
        bob.Should().BeEquivalentTo(new
        {
            Created = 1, Joined = 1, Played = 1, WithOtherHumans = 1, Finished = 0, Won = 0, Abandoned = 1,
            TilesPlayed = 4, Draws = 0
        });
        bob.Names.Should().Equal("Bob", "Bobby");
        bob.FirstSeen.Should().Be(T0.AddMinutes(10));
        bob.LastSeen.Should().Be(T0.AddMinutes(20));
    }

    [Fact]
    public void Build_SetsTheOwnersDevicesApart_SoOtherPlayersGamesCanBeCounted()
    {
        var entries = ThreeGames();
        entries.Add(At(30, new JournalEntry { Kind = JournalKinds.Mine, Visitor = Alice }));
        entries.Add(At(31, new JournalEntry { Kind = JournalKinds.Mine, Visitor = Bob }));
        entries.Add(At(32, new JournalEntry { Kind = JournalKinds.NotMine, Visitor = Bob }));

        var report = GameStatistics.Build(entries, T0.AddHours(3));

        report.Owners.Should().BeEquivalentTo([Alice]);
        report.Players.Single(p => p.Key == Alice).IsOwner.Should().BeTrue();
        report.Players.Single(p => p.Key == Bob).IsOwner.Should().BeFalse();
        report.Games.Where(report.HasOthers).Select(g => g.Id).Should().Equal("lone", "duo");
    }

    [Fact]
    public void Build_CountsASeatWithoutVisitorIdAsSomeoneElse_OnlyForThatGame()
    {
        List<JournalEntry> entries =
        [
            At(0, Created("g1", "Carol", null)),
            At(1, Started("g1", new("Carol", false, null), new("AI 1", true, null))),
            At(2, Created("g2", "Carol", null)),
            At(3, new JournalEntry { Kind = JournalKinds.Mine, Visitor = Alice })
        ];

        var report = GameStatistics.Build(entries, T0.AddHours(3));

        report.Players.Should().HaveCount(2).And.OnlyContain(p => !p.HasVisitorId);
        report.Games.Should().OnlyContain(g => report.HasOthers(g));
    }
}
