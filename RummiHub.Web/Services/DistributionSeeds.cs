using System.Security.Cryptography;

namespace RummiHub.Web.Services;

/// <summary>
/// Shuffle seeds. A normal game draws one from a CSPRNG: 32 bytes written as 64 hexadecimal
/// characters. Older games were dealt from 8-character codes, still accepted so they replay.
/// A seed fully determines the deal (every rack and the draw order).
/// </summary>
public static class DistributionSeeds
{
    public const int EntropyBytes = 32;

    /// <summary>Characters in a seed drawn today (<see cref="Create"/>).</summary>
    public const int Length = EntropyBytes * 2;

    /// <summary>Characters in an older replay code (System.Random deal).</summary>
    public const int LegacyLength = 8;

    public static string Create() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(EntropyBytes)).ToLowerInvariant();

    /// <summary>
    /// True for the shape <see cref="Create"/> produces (64 lowercase hex characters). Such a
    /// seed is dealt from its full 256 bits; older replay codes keep their System.Random deal.
    /// </summary>
    public static bool IsGenerated(string? seed) => seed is { Length: Length } && IsLowerHex(seed);

    /// <summary>True for a normalized seed: 64 or 8 lowercase hexadecimal characters.</summary>
    public static bool IsValid(string? seed) =>
        seed is { Length: Length or LegacyLength } && IsLowerHex(seed);

    /// <summary>
    /// Reads a typed or pasted seed: whitespace anywhere (a seed copied across a line break)
    /// is dropped and letters are lowercased. On failure, <paramref name="error"/> says why,
    /// with the length expected, so a seed cut short by a copy is not taken for another one.
    /// </summary>
    public static bool TryNormalize(string? input, out string seed, out string? error)
    {
        seed = new string((input ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray())
            .ToLowerInvariant();

        if (seed.Length == 0)
            error = "Paste a seed.";
        else if (!IsLowerHex(seed))
            error = "A seed only has the characters 0-9 and a-f.";
        else if (seed.Length is not (Length or LegacyLength))
            error = $"A seed has {Length} characters (older ones {LegacyLength}); this one has {seed.Length}.";
        else
            error = null;

        return error is null;
    }

    /// <summary>The first and last 8 characters, for a seed shown where 64 do not fit.</summary>
    public static string Abbreviate(string seed) =>
        seed.Length <= 20 ? seed : $"{seed[..8]}…{seed[^8..]}";

    private static bool IsLowerHex(string s) =>
        s.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
