using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RummiHub.Web.Services;

/// <summary>
/// Shuffle seeds. A new game draws 32 bytes from a CSPRNG and shows them as 64 hexadecimal
/// characters. The same characters always deal the same tiles. A game code is a different
/// thing: 8 characters that only join a room.
/// </summary>
public static partial class DistributionSeeds
{
    public const int EntropyBytes = 32;
    public const int HexLength = EntropyBytes * 2;
    public const int MaxLength = 64;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePattern();

    public static string Create() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(EntropyBytes)).ToLowerInvariant();

    /// <summary>
    /// True for the shape <see cref="Create"/> produces (64 lowercase hex characters). Such a
    /// seed is dealt from its full 256 bits; older replay ids keep their <c>System.Random</c> deal.
    /// </summary>
    public static bool IsGenerated(string? seed) =>
        seed is { Length: HexLength }
        && seed.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Legacy replay ids: short tokens that must not be an arbitrary config write.</summary>
    public static bool IsSafe(string? seed)
    {
        if (string.IsNullOrWhiteSpace(seed) || seed.Length > MaxLength)
            return false;

        return SafePattern().IsMatch(seed);
    }

    /// <summary>
    /// Interprets a pasted or stored seed. Whitespace and dashes are ignored when the rest is
    /// 64 hex digits, so a grouped paste still counts. The canonical form of a new seed is
    /// lowercase. Older tokens keep their exact spelling, because their deal depends on it.
    /// </summary>
    public static SeedInput Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new SeedInput(SeedInputKind.Empty, "", "");

        var trimmed = raw.Trim();
        var compact = Compact(trimmed);
        if (compact.Length == HexLength && compact.All(IsHex))
            return new SeedInput(SeedInputKind.Generated, compact.ToLowerInvariant(), "");

        // Older saved ids are short tokens, including the original 8-character hex ids.
        // Any other all-hex string is a mistyped 64-character seed, not a legacy id.
        if (IsSafe(trimmed) && (!trimmed.All(IsHex) || trimmed.Length == 8))
            return new SeedInput(SeedInputKind.Legacy, trimmed, "");

        return new SeedInput(SeedInputKind.Invalid, "", InvalidMessage(compact));
    }

    public static int CompactLength(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? 0 : Compact(raw.Trim()).Length;

    public static string Describe(string seed) =>
        IsGenerated(seed)
            ? "64 hexadecimal characters"
            : $"{seed.Length} characters · older seed";

    /// <summary>Compact size, for a row that already explains the format nearby.</summary>
    public static string SizeLabel(string seed) =>
        IsGenerated(seed) ? "64 hex" : $"{seed.Length} chars";

    /// <summary>Short form that fits a card. The full value stays available on hover and on copy.</summary>
    public static string Abbreviate(string seed)
    {
        if (string.IsNullOrEmpty(seed) || seed.Length <= 13)
            return seed;

        return string.Concat(seed.AsSpan(0, 8), "…", seed.AsSpan(seed.Length - 4));
    }

    /// <summary>
    /// Shown when a short token is not 64 hex and is not already on the proposed list.
    /// The token itself is included only when it is 8 hex digits (a game code typed in the
    /// wrong field). Anything else is left out of the message.
    /// </summary>
    public static string LegacyNotListedMessage(string seed)
    {
        if (seed.Length == 8 && seed.All(IsHex))
            return $"\"{seed.ToLowerInvariant()}\" is 8 characters, the size of a game code. " +
                   "A distribution seed is 64 hexadecimal characters (0-9, a-f). " +
                   "Paste a game code in Game code — it only joins a room. " +
                   "Older 8-character seeds work only when they are already in the proposed list.";

        return "That is not a 64-character seed, and it is not in the proposed list. " +
               "A distribution seed is 64 hexadecimal characters (0-9, a-f).";
    }

    private static string InvalidMessage(string compact)
    {
        if (compact.Length == 8 && compact.All(IsHex))
            return $"\"{compact.ToLowerInvariant()}\" is 8 characters, the size of a game code. " +
                   "A distribution seed is 64 hexadecimal characters (0-9, a-f). " +
                   "Paste a game code in Game code — it only joins a room.";

        if (compact.Length > 0 && compact.All(IsHex))
            return $"A distribution seed is exactly 64 hexadecimal characters (0-9, a-f). This one is {compact.Length}.";

        return "A distribution seed is exactly 64 hexadecimal characters (0-9, a-f). The text pasted is not a seed.";
    }

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

    private static bool IsHex(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
}

public enum SeedInputKind
{
    Empty,
    Generated,
    Legacy,
    Invalid
}

public readonly record struct SeedInput(SeedInputKind Kind, string Normalized, string Error);

/// <summary>A pasted seed the server refuses. The message is safe to show to the player.</summary>
public sealed class SeedInputException(string message) : ArgumentException(message);
