namespace RummiHub.Core.Models;

/// <summary>Where a set lies on the table: its row, and the column of its first tile in half tiles.</summary>
public readonly record struct BoardSpot(int Row, int Col);

/// <summary>
/// Lays the board out like tiles on a real table: every set has a spot on a grid and keeps
/// it from one move to the next, instead of flowing in a list where a set that grows pushes
/// everything after it. Columns are half tiles (a tile is <see cref="TileWidth"/> wide, two
/// sets on a row are at least <see cref="Gap"/> apart), rows are rows of tiles.
/// <list type="bullet">
/// <item>A set the move leaves alone never moves.</item>
/// <item>A set the move changes lies where the most of its tiles stay on their cell: a tile
/// added at the low end of a run extends it to the left, a run cut in two leaves each half
/// where it was. When that room is taken, that set alone moves to the nearest free room
/// (the row below rather than the row above), and nothing else is pushed.</item>
/// <item>A set played whole from the hand takes the first free room in reading order, with
/// <see cref="Room"/> around it while the rows in use have that much, so a set can grow by a
/// tile without bumping into its neighbour.</item>
/// </list>
/// A spot left empty stays empty until a later set takes it. During a move a set never lands
/// on cells still held by a previous set it takes nothing from: the page tells a move step by
/// step (tile-animations.js), and a tile must not rest on another one between two steps.
/// </summary>
public static class BoardGrid
{
    /// <summary>Columns a tile takes.</summary>
    public const int TileWidth = 2;

    /// <summary>Fewest free columns between two sets on a row.</summary>
    public const int Gap = 1;

    /// <summary>Free columns a set played from the hand gets around it when the rows in use allow.</summary>
    public const int Room = 3;

    /// <summary>A row further counts as this many columns when looking for the nearest room.</summary>
    private const int RowPitch = 4;

    /// <summary>Columns a set takes.</summary>
    public static int Width(ValidSet set) => TileWidth * set.Tiles.Length;

    /// <summary>
    /// The spots of <paramref name="next"/> (same order) on a table <paramref name="columns"/>
    /// wide, given the <paramref name="spots"/> of the <paramref name="previous"/> board. A set
    /// longer than the table lies at the start of a row and sticks out of it. With no previous
    /// board, the sets are laid in order from the top left.
    /// </summary>
    public static BoardSpot[] Place(IReadOnlyList<ValidSet> previous, IReadOnlyList<BoardSpot> spots,
        IReadOnlyList<ValidSet> next, int columns)
    {
        if (spots.Count != previous.Count)
            throw new ArgumentException("One spot per previous set.", nameof(spots));

        var sources = Sources(previous, next, out var unchanged, out var givers);
        var placed = new BoardSpot?[next.Count];
        var rows = spots.Count == 0 ? 0 : spots.Max(spot => spot.Row) + 1;

        // Sets the move leaves alone first: nothing may take their room.
        for (var j = 0; j < next.Count; j++)
            if (unchanged[j] >= 0) placed[j] = spots[unchanged[j]];

        // A previous set the move changed (or one it left alone, already laid) holds its cells
        // until the step that changes it: only a set taking tiles from it may land there.
        var held = new List<(int Prev, int Row, int From, int To)>();
        var kept = new HashSet<int>(unchanged.Where(i => i >= 0));
        for (var i = 0; i < previous.Count; i++)
            if (!kept.Contains(i))
                held.Add((i, spots[i].Row, spots[i].Col, spots[i].Col + Width(previous[i])));

        bool Free(int j, int row, int col, int gap)
        {
            var from = col - gap;
            var to = col + Width(next[j]) + gap;
            if (col < 0 || col + Width(next[j]) > Math.Max(columns, Width(next[j]))) return false;
            for (var other = 0; other < next.Count; other++)
                if (placed[other] is { } spot && spot.Row == row
                    && spot.Col < to && from < spot.Col + Width(next[other]))
                    return false;
            foreach (var (prev, heldRow, heldFrom, heldTo) in held)
                if (heldRow == row && heldFrom < to && from < heldTo && !givers[j].Contains(prev))
                    return false;
            return true;
        }

        // Sets made of board tiles, the ones keeping the most tiles first: where the most of
        // them stay on their cell, else the nearest free room.
        var rebuilt = Enumerable.Range(0, next.Count)
            .Where(j => placed[j] is null && sources[j].Any(source => source.Prev >= 0))
            .OrderByDescending(j => sources[j].Count(source => source.Prev >= 0))
            .ThenBy(j => j);
        foreach (var j in rebuilt)
        {
            // Where each tile coming from the board would keep its cell.
            var stays = new Dictionary<BoardSpot, int>();
            for (var b = 0; b < sources[j].Length; b++)
            {
                var (prev, seat) = sources[j][b];
                if (prev < 0) continue;
                var spot = new BoardSpot(spots[prev].Row, spots[prev].Col + TileWidth * (seat - b));
                stays[spot] = stays.GetValueOrDefault(spot) + 1;
            }

            var anchor = stays.OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Key.Row).ThenBy(entry => entry.Key.Col).First().Key;
            var fromBoard = sources[j].Count(source => source.Prev >= 0);

            (int Moved, int Far, int Up, int Row, int Col) best = default;
            var found = false;
            for (var row = 0; row <= rows; row++)
            for (var col = 0; col <= Math.Max(0, columns - Width(next[j])); col++)
            {
                if (!Free(j, row, col, Gap)) continue;
                var moved = fromBoard - stays.GetValueOrDefault(new BoardSpot(row, col));
                var far = (row - anchor.Row) * (row - anchor.Row) * RowPitch * RowPitch
                          + (col - anchor.Col) * (col - anchor.Col);
                var candidate = (moved, far, row < anchor.Row ? 1 : 0, row, col);
                if (found && candidate.CompareTo(best) >= 0) continue;
                best = candidate;
                found = true;
            }

            placed[j] = new BoardSpot(best.Row, best.Col);
            rows = Math.Max(rows, best.Row + 1);
        }

        // Sets played whole from the hand, in order: the first room in reading order.
        for (var j = 0; j < next.Count; j++)
        {
            if (placed[j] is not null) continue;
            var spot = FirstFree(j, Room) ?? FirstFree(j, Gap) ?? new BoardSpot(rows, 0);
            placed[j] = spot;
            rows = Math.Max(rows, spot.Row + 1);
        }

        return placed.Select(spot => spot!.Value).ToArray();

        BoardSpot? FirstFree(int j, int gap)
        {
            for (var row = 0; row < rows; row++)
            for (var col = 0; col <= Math.Max(0, columns - Width(next[j])); col++)
                if (Free(j, row, col, gap)) return new BoardSpot(row, col);
            return null;
        }
    }

    /// <summary>
    /// Where each tile of each next set comes from: (previous set, seat in it), or (-1, -1)
    /// for a tile from the hand. A set keeps the tiles of the previous set paired with it
    /// (<see cref="BoardSeating.Pair"/>), matched in set order like the page does; every other
    /// tile comes from a previous set that gave one like it away, the set giving the most
    /// first. <paramref name="unchanged"/>: the previous set a next set is, tile for tile, or
    /// -1. <paramref name="givers"/>: the previous sets each next set takes tiles from.
    /// </summary>
    /// <summary>
    /// Where each tile of each next set comes from: (previous set, seat in it), or (-1, -1)
    /// for a tile from the hand; the same matching <see cref="Place"/> lays the sets with.
    /// </summary>
    public static (int Prev, int Seat)[][] Sources(IReadOnlyList<ValidSet> previous, IReadOnlyList<ValidSet> next) =>
        Sources(previous, next, out _, out _);

    private static (int Prev, int Seat)[][] Sources(IReadOnlyList<ValidSet> previous, IReadOnlyList<ValidSet> next,
        out int[] unchanged, out HashSet<int>[] givers)
    {
        var pairs = BoardSeating.Pair(previous, next);
        var sources = new (int Prev, int Seat)[next.Count][];
        unchanged = new int[next.Count];
        givers = new HashSet<int>[next.Count];

        // Seats of each previous set still to hand out, by tile.
        var left = new Dictionary<int, Queue<int>>[previous.Count];
        for (var i = 0; i < previous.Count; i++)
        {
            left[i] = [];
            for (var seat = 0; seat < previous[i].Tiles.Length; seat++)
            {
                var key = BoardSeating.TileKey(previous[i].Tiles[seat]);
                if (!left[i].TryGetValue(key, out var seats)) left[i][key] = seats = new Queue<int>();
                seats.Enqueue(seat);
            }
        }

        for (var j = 0; j < next.Count; j++)
        {
            sources[j] = new (int, int)[next[j].Tiles.Length];
            Array.Fill(sources[j], (-1, -1));
            givers[j] = [];
            unchanged[j] = -1;
            var prev = pairs[j].PrevIndex;
            if (prev < 0) continue;

            givers[j].Add(prev);
            var inPlace = previous[prev].Tiles.Length == next[j].Tiles.Length;
            for (var b = 0; b < next[j].Tiles.Length; b++)
            {
                if (!left[prev].TryGetValue(BoardSeating.TileKey(next[j].Tiles[b]), out var seats)
                    || seats.Count == 0)
                {
                    inPlace = false;
                    continue;
                }

                var seat = seats.Dequeue();
                sources[j][b] = (prev, seat);
                inPlace &= seat == b;
            }

            if (inPlace) unchanged[j] = prev;
        }

        for (var j = 0; j < next.Count; j++)
        {
            if (unchanged[j] >= 0) continue;
            var missing = Enumerable.Range(0, sources[j].Length).Where(b => sources[j][b].Prev < 0).ToList();
            while (missing.Count > 0)
            {
                var giver = -1;
                var most = 0;
                for (var i = 0; i < previous.Count; i++)
                {
                    var gives = missing.GroupBy(b => BoardSeating.TileKey(next[j].Tiles[b]))
                        .Sum(group => Math.Min(group.Count(), left[i].GetValueOrDefault(group.Key)?.Count ?? 0));
                    if (gives > most) (giver, most) = (i, gives);
                }

                if (giver < 0) break;
                givers[j].Add(giver);
                missing.RemoveAll(b =>
                {
                    if (!left[giver].TryGetValue(BoardSeating.TileKey(next[j].Tiles[b]), out var seats)
                        || seats.Count == 0) return false;
                    sources[j][b] = (giver, seats.Dequeue());
                    return true;
                });
            }
        }

        return sources;
    }
}
