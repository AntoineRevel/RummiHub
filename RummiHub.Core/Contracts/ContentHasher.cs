using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

/// <summary>
/// Generates a deterministic hash of a solve request's content.
/// Two requests with the same hand and board produce the same hash,
/// enabling deduplication on the Redis side.
/// </summary>
public static class ContentHasher
{
    public static string ComputeHash(List<Tile> hand, Solution board, bool hasPlayed)
    {
        var sortedHand = hand
            .OrderBy(t => t.IsJoker)
            .ThenBy(t => t.Color)
            .ThenBy(t => t.Value)
            .ToList();

        var sortedSets = SortValidSets(board.Sets);

        var canonical = JsonSerializer.Serialize(
            new { Hand = sortedHand, Sets = sortedSets, HasPlayed = hasPlayed },
            RummiJsonOptions.Default);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hashBytes)[..16];
    }

    // Board order is cosmetic: sets are sorted on all their tiles, so a run and a group that
    // start with the same tile still land in the same order whatever the board shows.
    private static List<List<Tile>> SortValidSets(List<ValidSet> sets) =>
        sets
            .Select(s => s.Tiles.OrderBy(t => t.IsJoker).ThenBy(t => t.Color).ThenBy(t => t.Value).ToList())
            .OrderBy(SortKey, StringComparer.Ordinal)
            .ToList();

    private static string SortKey(List<Tile> tiles) =>
        string.Concat(tiles.Select(t => (char)('A' + (t.IsJoker ? 64 : 0) + (int)t.Color * 16 + t.Value)));
}
