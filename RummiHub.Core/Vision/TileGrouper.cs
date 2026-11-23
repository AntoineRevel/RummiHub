namespace RummiHub.Core.Vision;

/// <summary>A tile the detector found, in photo pixels (top-left origin).</summary>
public readonly record struct TileBox(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float CenterX => X + Width / 2;
    public float CenterY => Y + Height / 2;
}

/// <summary>
/// Turns the tiles found on a photo of the table into its sets, with geometry only: on a real
/// table the tiles of one set lie side by side on a line, and two sets are kept apart by a gap.
/// Sets come back in reading order (rows top to bottom, then left to right), which becomes the
/// order of the board's sets, and each set's tiles from left to right.
/// </summary>
public static class TileGrouper
{
    /// <summary>Largest gap between two tiles of the same set, in tile widths.</summary>
    public const float MaxGapInWidths = 0.3f;

    /// <summary>Largest vertical shift between two neighbours of the same set, in tile heights.</summary>
    public const float MaxShiftInHeights = 0.5f;

    /// <summary>Indices into <paramref name="boxes"/>, one list per set.</summary>
    public static List<List<int>> Group(IReadOnlyList<TileBox> boxes)
    {
        if (boxes.Count == 0) return [];

        // The median tile is the unit: a photo can be taken from any distance.
        var width = Median(boxes.Select(b => b.Width));
        var height = Median(boxes.Select(b => b.Height));

        var parent = Enumerable.Range(0, boxes.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        // Neighbours only link left-right: a set is a chain, so a slightly tilted photo still
        // keeps each set whole as long as each tile sits level with the next one.
        var byX = Enumerable.Range(0, boxes.Count).OrderBy(i => boxes[i].X).ToArray();
        for (var a = 0; a < byX.Length; a++)
        {
            var left = boxes[byX[a]];
            for (var b = a + 1; b < byX.Length; b++)
            {
                var right = boxes[byX[b]];
                var gap = right.X - left.Right;
                if (gap > MaxGapInWidths * width) break;
                if (Math.Abs(right.CenterY - left.CenterY) >= MaxShiftInHeights * height) continue;
                // A box overlapping its neighbour by more than half a tile is the same tile
                // seen twice or a tile on top of another: never a neighbour in a set.
                if (gap < -width / 2) continue;

                parent[Find(byX[b])] = Find(byX[a]);
            }
        }

        var sets = Enumerable.Range(0, boxes.Count)
            .GroupBy(Find)
            .Select(g => g.OrderBy(i => boxes[i].CenterX).ToList())
            .ToList();

        return ReadingOrder(sets, boxes, height);
    }

    /// <summary>
    /// The tiles of a photo of a rack, in reading order. A rack has no sets: only which tiles
    /// are there matters, the order just keeps the correction screen close to the photo.
    /// </summary>
    public static List<int> ReadRack(IReadOnlyList<TileBox> boxes) =>
        Group(boxes).SelectMany(set => set).ToList();

    private static List<List<int>> ReadingOrder(List<List<int>> sets, IReadOnlyList<TileBox> boxes, float height)
    {
        float Middle(List<int> set) => set.Average(i => boxes[i].CenterY);

        var rows = new List<List<List<int>>>();
        float rowMiddle = 0;
        foreach (var set in sets.OrderBy(Middle))
        {
            if (rows.Count == 0 || Middle(set) - rowMiddle > height / 2)
            {
                rows.Add([]);
                rowMiddle = Middle(set);
            }

            rows[^1].Add(set);
        }

        return rows
            .SelectMany(row => row.OrderBy(set => boxes[set[0]].X))
            .ToList();
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
