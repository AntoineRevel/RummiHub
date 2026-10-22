using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

public class SeedStoreTests
{
    private sealed class StubEnv(string environmentName, string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "RummiHub.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Persist_DoesNotWriteAppSettings_OutsideDevelopment()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seed-");
        var path = Path.Combine(dir.FullName, "appsettings.json");
        var original = """
                       {
                         "GameOptions": { "PredefinedIds": [ "15e005a3" ] }
                       }
                       """;
        File.WriteAllText(path, original);

        var store = new AppSettingsSeedStore(
            new StubEnv(Environments.Production, dir.FullName),
            NullLogger<AppSettingsSeedStore>.Instance);

        store.Persist("aabbccdd");
        store.Persist(new string('x', 10_000));
        store.Persist("{\"GameOptions\":null}");

        File.ReadAllText(path).Should().Be(original);
        dir.Delete(recursive: true);
    }

    [Fact]
    public void Persist_DoesNotCreateAFile_OutsideDevelopment()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seed-");
        var store = new AppSettingsSeedStore(
            new StubEnv(Environments.Production, dir.FullName),
            NullLogger<AppSettingsSeedStore>.Instance);

        store.Persist("aabbccdd");

        File.Exists(Path.Combine(dir.FullName, "appsettings.json")).Should().BeFalse();
        dir.Delete(recursive: true);
    }

    [Fact]
    public void Persist_InDevelopment_AppendsASafeSeed_AndIgnoresUnsafeOnes()
    {
        var dir = Directory.CreateTempSubdirectory("rummi-seed-");
        var path = Path.Combine(dir.FullName, "appsettings.json");
        File.WriteAllText(path, """{ "GameOptions": { "PredefinedIds": [ "15e005a3" ] } }""");

        var store = new AppSettingsSeedStore(
            new StubEnv(Environments.Development, dir.FullName),
            NullLogger<AppSettingsSeedStore>.Instance);

        store.Persist("  replay_1  ");
        store.Persist(new string('a', DistributionSeeds.MaxLength + 1));
        store.Persist("line\nbreak");

        var text = File.ReadAllText(path);
        text.Should().Contain("replay_1");
        text.Should().Contain("15e005a3");
        text.Should().NotContain("line");
        text.Should().NotContain(new string('a', DistributionSeeds.MaxLength + 1));
        dir.Delete(recursive: true);
    }
}
