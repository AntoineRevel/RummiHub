using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

/// <summary>
/// Génère un hash déterministe du contenu d'une requête de résolution.
/// Deux requêtes avec la même main et le même plateau produisent le même hash,
/// ce qui permet la déduplication côté Redis.
/// </summary>
public static class ContentHasher
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string ComputeHash(List<Tile> hand, Board board)
    {
        // Tri déterministe de la main
        var sortedHand = hand
            .OrderBy(t => t.Color)
            .ThenBy(t => t.Number)
            .ThenBy(t => t.IsJoker)
            .ToList();

        // Tri déterministe des groupes du plateau
        var sortedGroups = board.Groups
            .Select(g => new
            {
                Tiles = g.Tiles
                    .OrderBy(t => t.Color)
                    .ThenBy(t => t.Number)
                    .ThenBy(t => t.IsJoker)
                    .ToList(),
                g.Type
            })
            .OrderBy(g => g.Type)
            .ThenBy(g => g.Tiles.FirstOrDefault()?.Color)
            .ThenBy(g => g.Tiles.FirstOrDefault()?.Number)
            .ToList();

        var canonical = JsonSerializer.Serialize(
            new { Hand = sortedHand, Board = sortedGroups },
            CanonicalJsonOptions);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hashBytes)[..16];
    }
}
