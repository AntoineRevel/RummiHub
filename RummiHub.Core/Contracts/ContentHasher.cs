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

        var sortedRuns = SortValidSets(board.Runs);
        var sortedGroups = SortValidSets(board.Groups);

        var canonical = JsonSerializer.Serialize(
            new { Hand = sortedHand, Runs = sortedRuns, Groups = sortedGroups, HasPlayed = hasPlayed },
            RummiJsonOptions.Default);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hashBytes)[..16];
    }

    private static List<List<Tile>> SortValidSets(List<ValidSet> sets) =>
        sets
            .Select(s => s.Tiles.OrderBy(t => t.Color).ThenBy(t => t.Value).ToList())
            .OrderBy(tiles => tiles.FirstOrDefault().Color)
            .ThenBy(tiles => tiles.FirstOrDefault().Value)
            .ToList();
}
