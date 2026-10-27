using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class DistributionSeedParseTests
{
    [Fact]
    public void Parse_Accepts64Hex_IgnoringCaseSpacesAndDashes()
    {
        var seed = DistributionSeeds.Create();
        var grouped = string.Join('-', Enumerable.Range(0, 8).Select(i => seed.Substring(i * 8, 8)));

        DistributionSeeds.Parse(seed).Should().Be(new SeedInput(SeedInputKind.Generated, seed, ""));
        DistributionSeeds.Parse(seed.ToUpperInvariant()).Normalized.Should().Be(seed);
        DistributionSeeds.Parse("  " + grouped + "\n").Normalized.Should().Be(seed);
        DistributionSeeds.CompactLength(grouped).Should().Be(64);
    }

    [Fact]
    public void Parse_ExplainsAGameCode_WithoutTreatingItAsASeed()
    {
        var parsed = DistributionSeeds.Parse("e4fc4fab");

        parsed.Kind.Should().Be(SeedInputKind.Legacy);
        DistributionSeeds.LegacyNotListedMessage(parsed.Normalized)
            .Should().Contain("8 characters")
            .And.Contain("game code")
            .And.Contain("64 hexadecimal");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void Parse_RejectsTheWrongHexLength(string raw)
    {
        var parsed = DistributionSeeds.Parse(raw);

        parsed.Kind.Should().Be(SeedInputKind.Invalid);
        parsed.Error.Should().Contain("64 hexadecimal");
        parsed.Error.Should().NotContain(raw);
    }

    [Fact]
    public void Parse_RejectsJunk_WithoutEchoingIt()
    {
        var parsed = DistributionSeeds.Parse("../appsettings");

        parsed.Kind.Should().Be(SeedInputKind.Invalid);
        parsed.Error.Should().Contain("not a seed").And.NotContain("appsettings");
    }

    [Fact]
    public void Abbreviate_FitsACard_AndKeepsShortLegacyIds()
    {
        var seed = DistributionSeeds.Create();

        DistributionSeeds.Abbreviate(seed).Should().Be(seed[..8] + "…" + seed[^4..]);
        DistributionSeeds.Abbreviate("15e005a3").Should().Be("15e005a3");
        DistributionSeeds.Describe(seed).Should().Be("64 hexadecimal characters");
        DistributionSeeds.Describe("15e005a3").Should().Contain("older seed");
    }
}

public class FileSeedCatalogTests
{
    private static FileSeedCatalog Open(string path, params string[] bootstrap) =>
        new(path, bootstrap, NullLogger<FileSeedCatalog>.Instance);

    [Fact]
    public async Task NewFile_BootstrapsLegacyIds_OldestFirst_AndIgnoresUnsafeOnes()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seeds-");
        var path = Path.Combine(dir.FullName, "catalog.jsonl");

        var seeds = await Open(path, "15e005a3", "not safe", "76926024", "15e005a3").ListAsync();

        seeds.Select(seed => seed.Seed).Should().Equal("76926024", "15e005a3");
        seeds[0].AddedAt.Should().BeAfter(seeds[1].AddedAt);
    }

    [Fact]
    public async Task Add_AppendsNewestFirst_SurvivesANewProcess_AndDoesNotRewriteOldLines()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seeds-");
        var path = Path.Combine(dir.FullName, "catalog.jsonl");
        var first = new FileSeedCatalog(path, ["15e005a3"], NullLogger<FileSeedCatalog>.Instance);
        var older = DistributionSeeds.Create();
        var newer = DistributionSeeds.Create();

        (await first.AddAsync(older)).Status.Should().Be(SeedAddStatus.Added);
        File.AppendAllText(path, "NOT JSON\n");
        (await first.AddAsync("  " + newer.ToUpperInvariant() + "  ")).Status.Should().Be(SeedAddStatus.Added);
        (await first.AddAsync(older)).Status.Should().Be(SeedAddStatus.AlreadyPresent);

        var reloaded = Open(path, "should-not-rebootstrap");
        (await reloaded.ListAsync()).Select(seed => seed.Seed).Should().Equal(newer, older, "15e005a3");
        var text = File.ReadAllText(path);
        text.Should().Contain("NOT JSON");
        text.Should().NotContain("should-not-rebootstrap");
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(4);
    }

    [Fact]
    public async Task Add_RejectsAShortToken_AndStopsAtTheCap()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seeds-");
        var path = Path.Combine(dir.FullName, "catalog.jsonl");
        var catalog = new FileSeedCatalog(path, [], NullLogger<FileSeedCatalog>.Instance, maxEntries: 1);
        var first = DistributionSeeds.Create();
        var second = DistributionSeeds.Create();

        (await catalog.AddAsync("replay-me")).Status.Should().Be(SeedAddStatus.Invalid);
        (await catalog.AddAsync(first)).Status.Should().Be(SeedAddStatus.Added);
        var full = await catalog.AddAsync(second);

        full.Status.Should().Be(SeedAddStatus.Invalid);
        full.Error.Should().Contain("full");
        (await catalog.ListAsync()).Select(seed => seed.Seed).Should().Equal(first);
    }

    [Fact]
    public async Task AlreadyListedLegacySeed_IsNotAnError()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seeds-");
        var catalog = Open(Path.Combine(dir.FullName, "catalog.jsonl"), "15e005a3");

        (await catalog.AddAsync("15e005a3")).Status.Should().Be(SeedAddStatus.AlreadyPresent);
    }
}

public class RemoteSeedCatalogTests
{
    [Fact]
    public async Task ListAndAdd_UseTheSharedEndpoint_AndKeepNewestFirst()
    {
        var older = new CatalogSeed(DistributionSeeds.Create(), DateTimeOffset.Parse("2020-01-01T00:00:00Z"));
        var newer = new CatalogSeed(DistributionSeeds.Create(), DateTimeOffset.Parse("2024-01-01T00:00:00Z"));
        var seen = new List<HttpRequestMessage>();
        var handler = new StubHandler(request =>
        {
            seen.Add(request);
            if (request.Method == HttpMethod.Get)
            {
                var json = JsonSerializer.Serialize(new[] { older, newer }, SeedCatalogJson.Options);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"status":"added"}""", Encoding.UTF8, "application/json")
            };
        });
        var catalog = new RemoteSeedCatalog(new SingleClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://seed-catalog:8080/")
        }), NullLogger<RemoteSeedCatalog>.Instance);

        var listed = await catalog.ListAsync();
        (await catalog.AddAsync(newer.Seed)).Status.Should().Be(SeedAddStatus.Added);

        listed.Select(seed => seed.Seed).Should().Equal(newer.Seed, older.Seed);
        seen.Should().HaveCount(2);
        seen.Should().OnlyContain(request => request.RequestUri!.AbsolutePath == SeedCatalogApi.Path);
    }

    [Fact]
    public async Task Add_TurnsARefusalIntoAnUnavailableCatalog()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var catalog = new RemoteSeedCatalog(new SingleClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://seed-catalog:8080/")
        }), NullLogger<RemoteSeedCatalog>.Instance);

        var act = async () => await catalog.AddAsync(DistributionSeeds.Create());

        await act.Should().ThrowAsync<SeedCatalogUnavailableException>();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

public class SeedCatalogApiTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Authorize_AcceptsOnlyTheSharedKey()
    {
        var context = new DefaultHttpContext();
        SeedCatalogApi.Authorize(context.Request, Key).Should().BeFalse();

        context.Request.Headers[SeedCatalogApi.KeyHeader] = "nope";
        SeedCatalogApi.Authorize(context.Request, Key).Should().BeFalse();

        context.Request.Headers[SeedCatalogApi.KeyHeader] = Key;
        SeedCatalogApi.Authorize(context.Request, Key).Should().BeTrue();
        SeedCatalogApi.IsKeyStrong("short").Should().BeFalse();
        SeedCatalogApi.IsKeyStrong(Key).Should().BeTrue();
    }

    [Fact]
    public void RateGate_StopsAfterTheMinuteBudget()
    {
        var gate = new SeedCatalogRateGate();
        var now = DateTimeOffset.UnixEpoch;

        for (var i = 0; i < 2; i++)
            gate.TryEnter(now, maxPerMinute: 2).Should().BeTrue();

        gate.TryEnter(now.AddSeconds(1), maxPerMinute: 2).Should().BeFalse();
        gate.TryEnter(now.AddMinutes(1), maxPerMinute: 2).Should().BeTrue();
    }
}
