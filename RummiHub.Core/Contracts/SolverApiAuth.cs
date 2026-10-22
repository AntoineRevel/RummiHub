using System.Security.Cryptography;
using System.Text;

namespace RummiHub.Core.Contracts;

/// <summary>
/// Decides whether a caller may use the solver API. The API process is internal to the
/// Aspire network; this shared secret is the second lock if that process is reached anyway.
/// </summary>
public static class SolverApiAuth
{
    public const string HeaderName = "X-Solver-Key";

    /// <summary>Hard cap on a <c>/solve</c> body. A legal position is a few kilobytes.</summary>
    public const int MaxRequestBodyBytes = 32 * 1024;

    /// <summary>
    /// Returns true when <paramref name="presentedKey"/> matches <paramref name="configuredKey"/>.
    /// When no key is configured, anonymous calls are allowed only if
    /// <paramref name="allowAnonymousWhenUnconfigured"/> is true (Development).
    /// </summary>
    public static bool IsCallerAllowed(string? configuredKey, string? presentedKey, bool allowAnonymousWhenUnconfigured)
    {
        configuredKey = configuredKey?.Trim();
        presentedKey = presentedKey?.Trim();

        if (string.IsNullOrEmpty(configuredKey))
            return allowAnonymousWhenUnconfigured;

        if (string.IsNullOrEmpty(presentedKey) || presentedKey.Length != configuredKey.Length)
            return false;

        var expected = Encoding.UTF8.GetBytes(configuredKey);
        var presented = Encoding.UTF8.GetBytes(presentedKey);
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
