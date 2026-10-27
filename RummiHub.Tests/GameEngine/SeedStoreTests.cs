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

    private FileSeedStore CreateStore(params string[] initialSeeds) =>
        new(SeedsFile, initialSeeds, _time, NullLogger<FileSeedStore>.Instance);

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
    public void Add_HiddenOverAVisibleSeed_KeepsItVisible()
    {
        var store = CreateStore();
        store.Add(SeedA);

        store.Add(SeedA, _time.GetUtcNow().AddHours(24));

        store.GetAll().Single().HiddenUntil.Should().BeNull();
    }

    [Fact]
    public void Reveal_ShowsAHiddenSeed()
    {
        var store = CreateStore();
        store.Add(SeedA, _time.GetUtcNow().AddHours(24));
        store.GetAll().Single().IsVisible(_time.GetUtcNow()).Should().BeFalse();

        store.Reveal(SeedA);

        CreateStore().GetAll().Single().IsVisible(_time.GetUtcNow()).Should().BeTrue();
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
    public void BeforeTheFileExists_TheListIsTheInitialSeeds()
    {
        var store = CreateStore("15e005a3", "not-a-seed", "15e005a3");

        store.GetAll().Select(s => s.Seed).Should().Equal("15e005a3");
        File.Exists(SeedsFile).Should().BeFalse();

        store.Add(SeedA);

        CreateStore().GetAll().Select(s => s.Seed).Should().Equal(SeedA, "15e005a3");
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

    [Theory]
    [InlineData(" AAAA aaaa ", "aaaaaaaa")]
    [InlineData("15E005A3", "15e005a3")]
    public void TryNormalize_DropsWhitespace_AndLowercases(string input, string expected)
    {
        DistributionSeeds.TryNormalize(input, out var seed, out var error).Should().BeTrue();
        seed.Should().Be(expected);
        error.Should().BeNull();
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
}
