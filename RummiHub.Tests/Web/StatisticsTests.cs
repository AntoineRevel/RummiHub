using AwesomeAssertions;
using RummiHub.Web.Services;

namespace RummiHub.Tests.Web;

public class StatisticsTests
{
    private static readonly UmamiOptions Options = new()
    {
        Websites = new(StringComparer.OrdinalIgnoreCase)
        {
            ["rummisolve.com"] = "prod-id",
            ["test.rummisolve.com"] = "test-id",
            ["empty.example"] = ""
        }
    };

    [Theory]
    [InlineData("rummisolve.com", "prod-id")]
    [InlineData("TEST.rummisolve.com", "test-id")]
    [InlineData("localhost", null)]
    [InlineData("empty.example", null)]
    [InlineData(null, null)]
    public void WebsiteFor_PicksTheSiteByHost(string? host, string? expected) =>
        Options.WebsiteFor(host).Should().Be(expected);

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(false, "", false)]
    [InlineData(false, "Bearer x", false)]
    [InlineData(false, "Basic YWRtaW46eA==", true)]
    public void AdminAccess_NeedsCaddysCheckedHeaderOutsideDevelopment(
        bool isDevelopment, string? authorization, bool expected) =>
        AdminAccess.Allows(isDevelopment, authorization).Should().Be(expected);
}
