namespace RummiHub.Core.Models;

/// <summary>
/// Orders a new board after the previous one so the table moves as little as possible.
/// Runs and groups are one list: a set keeps the seat of the previous set it shares the most
/// tiles with (<see cref="Pair"/>), and a set rebuilt out of another set's tiles sits right beside it. Only sets
/// made entirely of new tiles go last. The order is cosmetic: which tiles form which set is
/// never changed.
/// </summary>
public static class BoardSeating
{
    /// <summary>
    /// <paramref name="next"/> reordered after <paramref name="previous"/>: each previous set,
    /// in board order, gives its seat to the set paired with it (see <see cref="Pair"/>), then
    /// to the unpaired sets whose tiles mostly came from it. The pieces of a run cut into
    /// runs stay in the order they had in it, low end first. Same instances, new order.
    /// </summary>
    public static List<ValidSet> Seat(IReadOnlyList<ValidSet> previous, IReadOnlyList<ValidSet> next)
    {
        var pairs = Pair(previous, next);
        var home = new int[next.Count];
        for (var j = 0; j < next.Count; j++)
        {
            home[j] = pairs[j].PrevIndex;
            if (home[j] >= 0) continue;

            var bestShared = 0;
            for (var i = 0; i < previous.Count; i++)
            {
                var shared = SharedTiles(previous[i], next[j]);
                if (shared > bestShared) (home[j], bestShared) = (i, shared);
            }
        }

        var ordered = new List<ValidSet>(next.Count);
        for (var i = 0; i < previous.Count; i++)
        {
            ordered.AddRange(Enumerable.Range(0, next.Count)
                .Where(j => home[j] == i)
                .OrderBy(j => previous[i].IsGroup || next[j].IsGroup
                    ? pairs[j].PrevIndex == i ? -1 : int.MaxValue
                    : FirstSharedSeat(previous[i], next[j]))
                .Select(j => next[j]));
        }

        for (var j = 0; j < next.Count; j++)
            if (home[j] < 0) ordered.Add(next[j]);

        return ordered;
    }

    /// <summary>Where in <paramref name="from"/> the first tile it shares with <paramref name="set"/> sits.</summary>
    private static int FirstSharedSeat(ValidSet from, ValidSet set)
    {
        for (var seat = 0; seat < from.Tiles.Length; seat++)
        {
            var key = TileKey(from.Tiles[seat]);
            if (set.Tiles.Any(tile => TileKey(tile) == key)) return seat;
        }

        return from.Tiles.Length;
    }

    /// <summary>
    /// Pairs next sets with previous sets by multiset overlap, largest overlap first: the two
    /// sets sharing the most tiles are paired, then the two sharing the most among the rest,
    /// until no two share a tile. Ties go to the pair with the fewest other tiles (a set kept
    /// whole before one that grew), then to board order. Unpaired next sets get (-1, 0). The
    /// client pairs sets the same way.
    /// </summary>
    public static (int PrevIndex, int Shared)[] Pair(IReadOnlyList<ValidSet> previous, IReadOnlyList<ValidSet> next)
    {
        var pairs = new (int PrevIndex, int Shared)[next.Count];
        Array.Fill(pairs, (-1, 0));

        var overlaps = new List<(int Shared, int Others, int Prev, int Next)>();
        for (var i = 0; i < previous.Count; i++)
        for (var j = 0; j < next.Count; j++)
        {
            var shared = SharedTiles(previous[i], next[j]);
            if (shared > 0)
                overlaps.Add((shared, previous[i].Tiles.Length + next[j].Tiles.Length - 2 * shared, i, j));
        }

        overlaps.Sort((a, b) => a.Shared != b.Shared ? b.Shared.CompareTo(a.Shared)
            : a.Others != b.Others ? a.Others.CompareTo(b.Others)
            : a.Prev != b.Prev ? a.Prev.CompareTo(b.Prev)
            : a.Next.CompareTo(b.Next));

        var taken = new bool[previous.Count];
        foreach (var (shared, _, i, j) in overlaps)
        {
            if (taken[i] || pairs[j].PrevIndex >= 0) continue;
            taken[i] = true;
            pairs[j] = (i, shared);
        }

        return pairs;
    }

    /// <summary>
    /// Tiles of <paramref name="next"/> that do not stay in the set they sat in: tiles coming
    /// from the hand plus board tiles changing set. What the player sees move.
    /// </summary>
    public static int MovedTiles(IReadOnlyList<ValidSet> previous, IReadOnlyList<ValidSet> next)
    {
        var pairs = Pair(previous, next);
        var moved = 0;
        for (var j = 0; j < next.Count; j++)
            moved += next[j].Tiles.Length - pairs[j].Shared;
        return moved;
    }

    /// <summary>Multiset intersection by tile identity, occurrence-aware; all jokers fold into one bucket.</summary>
    public static int SharedTiles(ValidSet a, ValidSet b)
    {
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
