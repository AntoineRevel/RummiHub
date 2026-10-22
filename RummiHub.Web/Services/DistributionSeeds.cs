using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RummiHub.Web.Services;

/// <summary>
/// Shuffle seeds. A normal game draws one from a CSPRNG. A caller may select a seed only
/// when it is already on the server list, and only when it matches <see cref="IsSafe"/>
/// so it cannot be an arbitrary-length config write.
/// </summary>
public static partial class DistributionSeeds
{
    public const int EntropyBytes = 32;
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePattern();

    public static string Create() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(EntropyBytes)).ToLowerInvariant();

    /// <summary>
    /// True for the shape <see cref="Create"/> produces (64 lowercase hex characters). Such a
    /// seed is dealt from its full 256 bits; older replay ids keep their System.Random deal.
    /// </summary>
    public static bool IsGenerated(string? seed) =>
        seed is { Length: EntropyBytes * 2 }
        && seed.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSafe(string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed) || seed.Length > MaxLength)
            return false;

        return SafePattern().IsMatch(seed);
    }
}
