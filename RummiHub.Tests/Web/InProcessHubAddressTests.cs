using FluentAssertions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.Web;

public class InProcessHubAddressTests
{
    [Fact]
    public void TryGetBaseAddress_ReturnsNullWhenNothingIsUsable()
    {
        InProcessHubAddress.TryGetBaseAddress(null).Should().BeNull();
        InProcessHubAddress.TryGetBaseAddress([]).Should().BeNull();
        InProcessHubAddress.TryGetBaseAddress(["not a url"]).Should().BeNull();
    }

    [Theory]
    [InlineData("http://[::]:8080", "http://127.0.0.1:8080")]
    [InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080")]
    [InlineData("http://+:8080", "http://127.0.0.1:8080")]
    [InlineData("http://*:8080", "http://127.0.0.1:8080")]
    public void TryGetBaseAddress_RewritesContainerWildcardsToLoopback(string listen, string expected)
    {
        InProcessHubAddress.TryGetBaseAddress([listen]).Should().Be(expected);
    }

    [Fact]
    public void TryGetBaseAddress_KeepsAspireLocalhostAndPrefersHttp()
    {
        InProcessHubAddress.TryGetBaseAddress(
            ["https://localhost:7178", "http://localhost:5126"])
            .Should().Be("http://localhost:5126");

        InProcessHubAddress.TryGetBaseAddress(["https://localhost:7178"])
            .Should().Be("https://localhost:7178");
    }
}
