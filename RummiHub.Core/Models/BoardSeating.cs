namespace RummiHub.Core.Models;

/// <summary>
/// Orders a new board after the previous one so the table moves as little as possible.
/// Runs and groups are one list: a set keeps the seat of the previous set it shares the most
/// tiles with, and a set rebuilt out of another set's tiles sits right beside it. Only sets
/// made entirely of new tiles go last. The order is cosmetic: which tiles form which set is
/// never changed.
/// </summary>
public static class BoardSeating
{
    /// <summary>
    /// <paramref name="next"/> reordered after <paramref name="previous"/>: each previous set,
    /// in board order, is followed by the set that took its seat (see <see cref="Pair"/>), then
    /// by the unseated sets whose tiles mostly came from it. Same instances, new order.
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
            for (var j = 0; j < next.Count; j++)
                if (pairs[j].PrevIndex == i) ordered.Add(next[j]);
            for (var j = 0; j < next.Count; j++)
                if (pairs[j].PrevIndex < 0 && home[j] == i) ordered.Add(next[j]);
        }

        for (var j = 0; j < next.Count; j++)
            if (home[j] < 0) ordered.Add(next[j]);

        return ordered;
    }

    /// <summary>
    /// Pairs each next set with a previous set by maximal multiset overlap, greedy in board
    /// order: every previous set takes the remaining next set it shares the most tiles with
    /// (at least one). Unpaired next sets get (-1, 0). The client pairs sets the same way.
    /// </summary>
    public static (int PrevIndex, int Shared)[] Pair(IReadOnlyList<ValidSet> previous, IReadOnlyList<ValidSet> next)
    {
        var pairs = new (int PrevIndex, int Shared)[next.Count];
        Array.Fill(pairs, (-1, 0));

        for (var i = 0; i < previous.Count; i++)
        {
            var best = -1;
            var bestShared = 0;
            for (var j = 0; j < next.Count; j++)
            {
                if (pairs[j].PrevIndex >= 0) continue;
                var shared = SharedTiles(previous[i], next[j]);
                if (shared > bestShared) (best, bestShared) = (j, shared);
            }

            if (best >= 0) pairs[best] = (i, bestShared);
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
