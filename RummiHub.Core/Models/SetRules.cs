namespace RummiHub.Core.Models;

/// <summary>
/// The Rummikub legality of one set, with every joker carrying the tile it stands for.
/// Shared by the worker's <c>SolutionValidator</c> and the photo reader's decoder, so both
/// accept exactly the same sets.
/// </summary>
public static class SetRules
{
    public static bool IsLegal(ValidSet set) => IsLegalRun(set) || IsLegalGroup(set);

    /// <summary>At least three consecutive values of one color (jokers fill gaps).</summary>
    public static bool IsLegalRun(ValidSet set)
    {
        var tiles = set.Tiles;
        if (tiles.Length < 3) return false;

        TileColor? color = null;
        var ordered = tiles.OrderBy(t => t.Value).ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            var tile = ordered[i];
            if (tile.Value is < 1 or > 13) return false;
            if (!tile.IsJoker)
            {
                if (color is null) color = tile.Color;
                else if (color != tile.Color) return false;
            }

            if (i > 0 && ordered[i].Value != ordered[i - 1].Value + 1) return false;
        }

        return true;
    }

    /// <summary>Three or four tiles of one value, each real tile in a different color.</summary>
    public static bool IsLegalGroup(ValidSet set)
    {
        var tiles = set.Tiles;
        if (tiles.Length is < 3 or > 4) return false;

        int? value = null;
        var seenColors = new HashSet<TileColor>();

        foreach (var tile in tiles)
        {
            if (tile.Value is < 1 or > 13) return false;
            if (tile.IsJoker) continue;

            if (value is null) value = tile.Value;
            else if (value != tile.Value) return false;

            if (!seenColors.Add(tile.Color)) return false;
        }

        return true;
    }
}
