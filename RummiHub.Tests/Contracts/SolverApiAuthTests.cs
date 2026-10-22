using FluentAssertions;
using RummiHub.Core.Contracts;

namespace RummiHub.Tests.Contracts;

public class SolverApiAuthTests
{
    [Fact]
    public void IsCallerAllowed_RefusesAnonymousCallsWhenUnconfiguredOutsideDevelopment()
    {
        SolverApiAuth.IsCallerAllowed(null, null, allowAnonymousWhenUnconfigured: false).Should().BeFalse();
        SolverApiAuth.IsCallerAllowed("  ", "anything", allowAnonymousWhenUnconfigured: false).Should().BeFalse();
    }

    [Fact]
    public void IsCallerAllowed_AllowsAnonymousCallsOnlyInDevelopmentWhenUnconfigured()
    {
        SolverApiAuth.IsCallerAllowed(null, null, allowAnonymousWhenUnconfigured: true).Should().BeTrue();
    }

    [Fact]
    public void IsCallerAllowed_RequiresTheConfiguredKeyEvenInDevelopment()
    {
        const string key = "correct-horse-battery";

        SolverApiAuth.IsCallerAllowed(key, key, allowAnonymousWhenUnconfigured: true).Should().BeTrue();
        SolverApiAuth.IsCallerAllowed(key, "wrong-horse-battery", allowAnonymousWhenUnconfigured: true).Should().BeFalse();
        SolverApiAuth.IsCallerAllowed(key, null, allowAnonymousWhenUnconfigured: true).Should().BeFalse();
        SolverApiAuth.IsCallerAllowed(key, "", allowAnonymousWhenUnconfigured: true).Should().BeFalse();
    }

    [Fact]
    public void IsCallerAllowed_TrimsAndRejectsADifferentLength()
    {
        SolverApiAuth.IsCallerAllowed("  secret  ", "secret", allowAnonymousWhenUnconfigured: false).Should().BeTrue();
        SolverApiAuth.IsCallerAllowed("secret", "secret-extra", allowAnonymousWhenUnconfigured: false).Should().BeFalse();
    }

    [Fact]
    public void MaxRequestBody_IsWellUnderTheFrameworkDefault()
    {
        SolverApiAuth.MaxRequestBodyBytes.Should().Be(32 * 1024);
        SolverApiOptions.DefaultPermitLimit.Should().BeGreaterThan(0);
        SolverApiOptions.DefaultWindowSeconds.Should().BeGreaterThan(0);
    }
}
