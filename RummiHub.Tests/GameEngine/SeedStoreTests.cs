using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public sealed class SeedStoreTests : IDisposable
{
    private static readonly string SeedA = new('a', DistributionSeeds.Length);
    private static readonly string SeedB = new('b', DistributionSeeds.Length);
    private static readonly string SeedC = new('c', DistributionSeeds.Length);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rummi-seed-");
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    private string SeedsFile => Path.Combine(_dir.FullName, "seeds", "seed-configs.json");

    private FileSeedStore CreateStore() =>
        new(SeedsFile, _time, NullLogger<FileSeedStore>.Instance);

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static string Id(string seed, int players = 2, int seat = 0) =>
        DistributionSeeds.ListId(seed, players, seat);

    [Fact]
    public void Add_ListsTheMostRecentlyAddedFirst_AndPersistsAcrossInstances()
    {
        var store = CreateStore();

        store.Add(SeedA, 3, 1, "Nice opening");
        _time.Advance(TimeSpan.FromMinutes(1));
        store.Add(SeedB, 2, 0, null);

        var seeds = CreateStore().GetAll();
        seeds.Select(s => s.Seed).Should().Equal(SeedB, SeedA);
        seeds[1].Should().Be(new SavedSeed(SeedA, 3, 1, "Nice opening", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Add_OfAListedEntry_MovesItUp_WithoutDuplicating_AndKeepsItsName()
    {
        var store = CreateStore();
        store.Add(SeedA, 2, 0, "First");
        _time.Advance(TimeSpan.FromMinutes(1));
        store.Add(SeedB, 2, 0, null);
        _time.Advance(TimeSpan.FromMinutes(1));

        store.Add(SeedA, 2, 0, "  ");

        store.GetAll().Select(s => (s.Seed, s.Name)).Should().Equal((SeedA, "First"), (SeedB, null));
    }

    [Fact]
    public void TheSameSeed_FromAnotherSeatOrTableSize_IsAnotherEntry()
    {
        var store = CreateStore();

        store.Add(SeedA, 2, 0, null);
        store.Add(SeedA, 2, 1, null);
        store.Add(SeedA, 3, 1, null);

        store.GetAll().Select(s => s.ListId).Should().BeEquivalentTo([Id(SeedA, 2, 0), Id(SeedA, 2, 1), Id(SeedA, 3, 1)]);
    }

    [Fact]
    public void Read_DropsEntriesWithoutATableSize_LikeTheOldSeedFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeedsFile)!);
        File.WriteAllText(SeedsFile, $$"""
            { "seeds": [
              { "seed": "{{SeedA}}", "addedAt": "2026-10-27T15:42:27Z" },
              { "seed": "{{SeedB}}", "players": 4, "seat": 3, "addedAt": "2026-10-27T15:42:27Z" },
              { "seed": "{{SeedC}}", "players": 2, "seat": 2, "addedAt": "2026-10-27T15:42:27Z" }
            ] }
            """);

        CreateStore().GetAll().Select(s => s.Seed).Should().Equal(SeedB);
    }

    [Fact]
    public void Rename_NamesTheEntry_AndAnEmptyNameClearsIt()
    {
        var store = CreateStore();
        store.Add(SeedA, 2, 0, null);

        CreateStore().Rename(Id(SeedA), "  Two\tjokers  in   hand ");
        store.GetAll().Single().Name.Should().Be("Two jokers in hand");

        store.Rename(Id(SeedA), "");
        store.GetAll().Single().Name.Should().BeNull();
    }

    [Fact]
    public void Rename_CutsALongName()
    {
        var store = CreateStore();
        store.Add(SeedA, 2, 0, new string('x', 100));

        store.GetAll().Single().Name.Should().HaveLength(SavedSeed.MaxNameLength);
    }

    [Fact]
    public void Remove_TakesTheEntryOffTheList_ForEveryStoreOnTheFile()
    {
        var store = CreateStore();
        store.Add(SeedA, 2, 0, null);
        _time.Advance(TimeSpan.FromMinutes(1));
        store.Add(SeedB, 2, 0, null);

        CreateStore().Remove(Id(SeedA));
        store.Remove(Id(SeedC));

        store.GetAll().Select(s => s.Seed).Should().Equal(SeedB);
    }

    [Theory]
    [InlineData("../appsettings", 2, 0)]
    [InlineData("AAAA", 2, 0)]
    [InlineData(null, 1, 0)]
    [InlineData(null, 5, 0)]
    [InlineData(null, 3, 3)]
    [InlineData(null, 3, -1)]
    public void Add_IgnoresAMalformedEntry(string? seed, int players, int seat)
    {
        var store = CreateStore();

        store.Add(seed ?? SeedA, players, seat, null);
        store.Add(SeedA.ToUpperInvariant(), 2, 0, null);

        store.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void TwoStoresOnTheSameFile_SeeEachOthersSeeds()
    {
        var production = CreateStore();
        var testSite = CreateStore();

        production.Add(SeedA, 2, 0, null);
        _time.Advance(TimeSpan.FromSeconds(1));
        testSite.Add(SeedB, 2, 0, null);

        production.GetAll().Select(s => s.Seed).Should().Equal(SeedB, SeedA);
    }

    [Fact]
    public void ACorruptFile_IsNotOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeedsFile)!);
        File.WriteAllText(SeedsFile, "{ not json");
        var store = CreateStore();

        store.GetAll().Should().BeEmpty();
        store.Add(SeedA, 2, 0, null);

        File.ReadAllText(SeedsFile).Should().Be("{ not json");
    }

    [Fact]
    public void Add_KeepsAtMostMaxSeeds_DroppingTheOldest()
    {
        var store = CreateStore();
        for (var i = 0; i <= FileSeedStore.MaxSeeds; i++)
        {
            store.Add(i.ToString("x64"), 2, 0, null);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        var seeds = store.GetAll();
        seeds.Should().HaveCount(FileSeedStore.MaxSeeds);
        seeds[0].Seed.Should().Be(FileSeedStore.MaxSeeds.ToString("x64"));
        seeds.Select(s => s.Seed).Should().NotContain(0.ToString("x64"));
    }

    [Fact]
    public void TryNormalize_RefusesAnEightCharacterValue()
    {
        DistributionSeeds.TryNormalize("15e005a3", out _, out var error).Should().BeFalse();
        error.Should().Contain("64").And.Contain("8");
    }

    [Fact]
    public void TryNormalize_ATruncatedSeed_SaysTheExpectedLength()
    {
        DistributionSeeds.TryNormalize(SeedC[..63], out _, out var error).Should().BeFalse();

        error.Should().Contain("64").And.Contain("63");
    }

    [Fact]
    public void TryNormalize_RefusesNonHexCharacters()
    {
        DistributionSeeds.TryNormalize("ggggggggg", out _, out var error).Should().BeFalse();
        error.Should().Contain("0-9");
    }

    [Fact]
    public void TryNormalize_AGroupedSixtyFour_IsTheSameSeed()
    {
        var grouped = string.Join("-", Enumerable.Range(0, 8).Select(i => SeedA.Substring(i * 8, 8)));

        DistributionSeeds.TryNormalize(grouped.ToUpperInvariant(), out var seed, out var error).Should().BeTrue();
        seed.Should().Be(SeedA);
        error.Should().BeNull();
    }

    [Fact]
    public void Abbreviate_KeepsTheFirst8AndLast4Characters()
    {
        DistributionSeeds.Abbreviate(SeedA).Should().Be($"{SeedA[..8]}…{SeedA[^4..]}");
    }
}
