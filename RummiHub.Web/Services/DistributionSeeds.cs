using System.Security.Cryptography;

namespace RummiHub.Web.Services;

/// <summary>
/// Shuffle seeds: 32 bytes from a CSPRNG, written as 64 hexadecimal characters. A seed
/// fully determines the deal (every rack and the draw order).
/// </summary>
public static class DistributionSeeds
{
    public const int EntropyBytes = 32;

    /// <summary>Characters in a seed.</summary>
    public const int Length = EntropyBytes * 2;

    public static string Create() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(EntropyBytes)).ToLowerInvariant();

    /// <summary>
    /// True for the shape <see cref="Create"/> produces (64 lowercase hex characters). Such a
    /// seed is dealt from its full 256 bits.
    /// </summary>
    public static bool IsGenerated(string? seed) => seed is { Length: Length } && IsLowerHex(seed);

    /// <summary>
    /// Reads a typed or pasted seed. Spaces and dashes (a seed copied in groups) are dropped
    /// and letters are lowercased. On failure, <paramref name="error"/> says why, with the
    /// length expected, so a seed cut short by a copy is not taken for another one.
    /// </summary>
    public static bool TryNormalize(string? input, out string seed, out string? error)
    {
        seed = new string((input ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '–' or '—'))
            .ToArray()).ToLowerInvariant();

        if (seed.Length == 0)
            error = "Paste a seed.";
        else if (!IsLowerHex(seed))
            error = "A seed only has the characters 0-9 and a-f.";
        else if (seed.Length != Length)
            error = $"A seed has {Length} characters; this one has {seed.Length}.";
        else
            error = null;

        return error is null;
    }

    /// <summary>Short form that fits a card or a select; the full value is the title.</summary>
    public static string Abbreviate(string seed) =>
        seed.Length <= 13 ? seed : $"{seed[..8]}…{seed[^4..]}";

    private static bool IsLowerHex(string s) =>
        s.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
