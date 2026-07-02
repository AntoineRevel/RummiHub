using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Computes stable per-tile "flip ids" for a board, used server-side by
/// <see cref="BoardTransition"/> to count how many tiles moved/entered between two boards
/// (which drives the animation duration and AI pacing).
///
/// Tiles of the same (value, color, isJoker) are visually interchangeable and can appear
/// in duplicate, so each tile gets an occurrence index within its type, assigned by walking
/// the board in render order: all Runs (left-to-right), then all Groups (left-to-right) —
/// the exact order used by <see cref="Components.Shared.SolutionComponent"/>.
/// All jokers fold into a single type bucket (matching <see cref="Tile.Equals"/>).
/// </summary>
public static class BoardFlipKeys
{
    /// <summary>
    /// Returns a flat list of flip ids aligned with the board's reading order
    /// (Runs then Groups), one entry per tile. The format is
    /// <c>{value}-{color}-{joker}#{occurrenceIndex}</c>, e.g. <c>7-Red#0</c> or <c>J#1</c>.
    /// </summary>
    public static List<string> Assign(Solution board)
    {
        var counts = new Dictionary<string, int>();
        var result = new List<string>();

        foreach (var set in board.Runs)
            foreach (var tile in set.Tiles)
                result.Add(NextKey(tile, counts));

        foreach (var set in board.Groups)
            foreach (var tile in set.Tiles)
                result.Add(NextKey(tile, counts));

        return result;
    }

    private static string NextKey(Tile tile, Dictionary<string, int> counts)
    {
        var type = tile.IsJoker ? "J" : $"{tile.Value}-{tile.Color}";
        var occurrence = counts.TryGetValue(type, out var c) ? c : 0;
        counts[type] = occurrence + 1;
        return $"{type}#{occurrence}";
    }
}
