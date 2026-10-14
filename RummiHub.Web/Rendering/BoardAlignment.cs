using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Cosmetically reorders a freshly-solved board so its runs/groups line up with the board
/// previously shown to the player. The solver rebuilds the board from scratch (and sorts runs
/// by length), so unchanged sets would otherwise jump to new screen positions and animate for
/// no reason. Aligning keeps unchanged sets in place, so only genuinely relocated or new tiles
/// move — honoring "move the fewest tiles possible". Only the ORDER of the Runs/Groups lists
/// changes; the partition (which tiles form which set) is untouched, so this has no game effect.
/// </summary>
public static class BoardAlignment
{
    public static Solution Align(Solution previous, Solution next) => new()
    {
        Runs = MatchAndOrder(previous.Runs, next.Runs),
        Groups = MatchAndOrder(previous.Groups, next.Groups),
        IsValid = next.IsValid
    };

    private static List<ValidSet> MatchAndOrder(List<ValidSet> previousSets, List<ValidSet> nextSets)
    {
        var remaining = new List<ValidSet>(nextSets);
        var ordered = new List<ValidSet>(nextSets.Count);

        // Each previous set, in display order, pulls forward the remaining new set it shares the
        // most tiles with (it must share at least one). Greedy by previous order.
        foreach (var previous in previousSets)
        {
            ValidSet? best = null;
            var bestShared = 0;
            foreach (var candidate in remaining)
            {
                var shared = SharedTileCount(previous, candidate);
                if (shared > bestShared)
                {
                    bestShared = shared;
                    best = candidate;
                }
            }

            if (best is null) continue;
            ordered.Add(best);
            remaining.Remove(best);
        }

        // Brand-new sets (no tiles in common with any previous set) keep their original order, last.
        ordered.AddRange(remaining);
        return ordered;
    }

    private static int SharedTileCount(ValidSet a, ValidSet b)
    {
        // Multiset intersection by tile identity; all jokers collapse to one bucket.
        var counts = new Dictionary<int, int>();
        foreach (var tile in a.Tiles)
        {
            var key = TileKey(tile);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var shared = 0;
        foreach (var tile in b.Tiles)
        {
            var key = TileKey(tile);
            if (counts.TryGetValue(key, out var count) && count > 0)
            {
                counts[key] = count - 1;
                shared++;
            }
        }

        return shared;
    }

    private static int TileKey(Tile tile) => tile.IsJoker ? -1 : (tile.Value << 2) | (int)tile.Color;
}
