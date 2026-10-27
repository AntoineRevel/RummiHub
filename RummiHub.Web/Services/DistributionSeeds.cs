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
    /// Reads a typed or pasted seed. Spaces and dashes (a seed copied in groups) are dropped
    /// and letters are lowercased. On failure, <paramref name="error"/> says why, with the
    /// length expected, so a seed cut short by a copy is not taken for another one.
    /// </summary>
    public static bool TryNormalize(string? input, out string seed, out string? error)
    {
        seed = Compact(input ?? string.Empty).ToLowerInvariant();

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

    /// <summary>
    /// Shown when an 8-character value is a room that exists on this server. The value is a
    /// game code (it only joins that room), not a distribution seed.
    /// </summary>
    public static string GameCodePastedMessage(string code) =>
        $"\"{code}\" is 8 characters, the size of a game code. " +
        "A seed is 64 hexadecimal characters (32 bytes) and deals the same tiles. " +
        "A game code only joins a room — paste it in Game code.";

    /// <summary>What the characters mean, for the line under a shown seed.</summary>
    public static string Describe(string seed) =>
        IsGenerated(seed)
            ? "64 hexadecimal characters (32 bytes)"
            : $"{seed.Length} characters · older seed";

    /// <summary>Compact size, next to a seed that is already shortened.</summary>
    public static string SizeLabel(string seed) =>
        IsGenerated(seed) ? "64 hex" : $"{seed.Length} chars";

    /// <summary>
    /// Short form that fits a card. The full value stays available on hover and on copy.
    /// </summary>
    public static string Abbreviate(string seed) =>
        seed.Length <= 13 ? seed : $"{seed[..8]}…{seed[^4..]}";

    private static string Compact(string value)
    {
        var buffer = new char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '–' or '—')
                continue;
            buffer[n++] = c;
        }

        return new string(buffer, 0, n);
    }

    private static bool IsLowerHex(string s) =>
        s.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
