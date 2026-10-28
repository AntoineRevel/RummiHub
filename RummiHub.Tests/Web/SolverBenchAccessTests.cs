using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RummiHub.Web.Services;

namespace RummiHub.Tests.Web;

public class SolverBenchAccessTests
{
    private sealed class StubEnv(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "RummiHub.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(string? enabled)
    {
        var values = new Dictionary<string, string?>();
        if (enabled is not null)
            values[SolverBenchAccess.ConfigKey] = enabled;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Fact]
    public void From_IsOnInDevelopmentByDefault()
    {
        SolverBenchAccess.From(Config(null), new StubEnv(Environments.Development))
            .Enabled.Should().BeTrue();
    }

    [Fact]
    public void From_IsOffInProductionByDefault()
    {
        SolverBenchAccess.From(Config(null), new StubEnv(Environments.Production))
            .Enabled.Should().BeFalse();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void From_ExplicitSettingOverridesTheEnvironment(string value, bool expected)
    {
        SolverBenchAccess.From(Config(value), new StubEnv(Environments.Production))
            .Enabled.Should().Be(expected);
        SolverBenchAccess.From(Config(value), new StubEnv(Environments.Development))
            .Enabled.Should().Be(expected);
    }
}
