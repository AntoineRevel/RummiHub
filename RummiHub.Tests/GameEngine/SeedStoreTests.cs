using FluentAssertions;
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

    private string SeedsFile => Path.Combine(_dir.FullName, "seeds", "seeds.json");

    private FileSeedStore CreateStore() =>
        new(SeedsFile, _time, NullLogger<FileSeedStore>.Instance);

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    [Fact]
    public void Add_ListsTheMostRecentlyAddedFirst_AndPersistsAcrossInstances()
    {
        var store = CreateStore();

        store.Add(SeedA);
        _time.Advance(TimeSpan.FromMinutes(1));
        store.Add(SeedB);

        CreateStore().GetAll().Select(s => s.Seed).Should().Equal(SeedB, SeedA);
    }

    [Fact]
    public void Add_OfAListedSeed_MovesItUp_WithoutDuplicating()
    {
        var store = CreateStore();
        store.Add(SeedA);
        _time.Advance(TimeSpan.FromMinutes(1));
        store.Add(SeedB);
        _time.Advance(TimeSpan.FromMinutes(1));

        store.Add(SeedA);

        store.GetAll().Select(s => s.Seed).Should().Equal(SeedA, SeedB);
    }

    [Fact]
    public void AFileWithTheOldHiddenUntilField_StillReadsEverySeed()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeedsFile)!);
        File.WriteAllText(SeedsFile, $$"""
            { "seeds": [ { "seed": "{{SeedA}}", "addedAt": "2026-10-27T15:42:27Z", "hiddenUntil": "2026-10-28T15:42:27Z" } ] }
            """);

        CreateStore().GetAll().Select(s => s.Seed).Should().Equal(SeedA);
    }

    [Fact]
    public void Add_IgnoresAMalformedSeed()
    {
        var store = CreateStore();

        store.Add("../appsettings");
        store.Add(SeedA.ToUpperInvariant());

        store.GetAll().Should().BeEmpty();
    }

    [Fact]
    public void TwoStoresOnTheSameFile_SeeEachOthersSeeds()
    {
        var production = CreateStore();
        var testSite = CreateStore();

        production.Add(SeedA);
        _time.Advance(TimeSpan.FromSeconds(1));
        testSite.Add(SeedB);

        production.GetAll().Select(s => s.Seed).Should().Equal(SeedB, SeedA);
    }

    [Fact]
    public void ACorruptFile_IsNotOverwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SeedsFile)!);
        File.WriteAllText(SeedsFile, "{ not json");
        var store = CreateStore();

        store.GetAll().Should().BeEmpty();
        store.Add(SeedA);

        File.ReadAllText(SeedsFile).Should().Be("{ not json");
    }

    [Fact]
    public void Add_KeepsAtMostMaxSeeds_DroppingTheOldest()
    {
        var store = CreateStore();
        for (var i = 0; i <= FileSeedStore.MaxSeeds; i++)
        {
            store.Add(i.ToString("x64"));
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
