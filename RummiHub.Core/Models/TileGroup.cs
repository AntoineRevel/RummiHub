namespace RummiHub.Core.Models;

/// <summary>
/// A valid group on the board: either a Run (consecutive same-color) or a Set (same number, different colors).
/// </summary>
public record TileGroup(List<Tile> Tiles, TileGroupType Type)
{
    public bool IsValid => Type switch
    {
        TileGroupType.Run => IsValidRun(),
        TileGroupType.Set => IsValidSet(),
        _ => false
    };

    private bool IsValidRun()
    {
        if (Tiles.Count < 3) return false;
        var nonJokers = Tiles.Where(t => !t.IsJoker).ToList();
        if (nonJokers.Count == 0) return false;

        var color = nonJokers[0].Color;
        if (nonJokers.Any(t => t.Color != color)) return false;

        var sorted = Tiles.OrderBy(t => t.Number).ToList();
        for (int i = 1; i < sorted.Count; i++)
        {
            if (!sorted[i].IsJoker && !sorted[i - 1].IsJoker &&
                sorted[i].Number != sorted[i - 1].Number + 1)
                return false;
        }
        return true;
    }

    private bool IsValidSet()
    {
        if (Tiles.Count < 3 || Tiles.Count > 4) return false;
        var nonJokers = Tiles.Where(t => !t.IsJoker).ToList();
        if (nonJokers.Count == 0) return false;

        var number = nonJokers[0].Number;
        if (nonJokers.Any(t => t.Number != number)) return false;

        var colors = nonJokers.Select(t => t.Color).ToHashSet();
        return colors.Count == nonJokers.Count;
    }
}

public enum TileGroupType
{
    Run,
    Set
}
