using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Single source of truth for the tile animation's tempo. The constants are pushed to the JS
/// module (<c>wwwroot/js/tile-animations.js</c>) via its <c>configure()</c> export, and
/// <see cref="EstimateMs"/> mirrors the client's board story so AI pacing
/// (<see cref="Models.Game.PendingAnimationMs"/>) never plays a move mid-animation.
/// </summary>
/// <remarks>
/// The client never moves the board before the play that causes it. Every board change is told
/// in three acts: (1) the placed tiles — played from the hand, or board tiles changing set —
/// take off one after the other and hover just above their slots; (2) the board makes room under
/// them in ONE glide; (3) they drop into place. With no room to make they fly straight in.
/// </remarks>
public static class BoardPacing
{
    /// <summary>A hand tile gliding between the rack and the play zone.</summary>
    public const int MoveMs = 350;

    /// <summary>A newcomer with no origin on screen popping in.</summary>
    public const int PopMs = 300;

    /// <summary>Act 1 — one placed tile's flight to its slot (or just above it).</summary>
    public const int FlightMs = 480;

    /// <summary>Act 1 — interval between two placed tiles taking off.</summary>
    public const int StaggerMs = 120;

    /// <summary>Act 2 — the board making room under the placed tiles, in one glide.</summary>
    public const int RoomMs = 420;

    /// <summary>Act 3 — one hovering tile dropping into its slot.</summary>
    public const int DropMs = 280;

    /// <summary>Act 3 — interval between two drops.</summary>
    public const int DropStaggerMs = 60;

    /// <summary>Slack absorbing client jitter.</summary>
    private const int BufferMs = 200;

    /// <summary>
    /// Estimated client animation duration (ms) for <paramref name="previous"/> →
    /// <paramref name="next"/>, which must already be aligned (<see cref="BoardAlignment"/>)
    /// so unchanged sets keep their reading-order index.
    /// </summary>
    public static int EstimateMs(Solution previous, Solution next)
        => DurationForPlacedTiles(CountPlacedTiles(previous, next));

    /// <summary>
    /// Duration of the board story placing <paramref name="placed"/> tiles. Conservative: it always
    /// counts acts 2 and 3, since whether the board really has to make room depends on the
    /// client's layout (line wrapping), unknown here.
    /// </summary>
    public static int DurationForPlacedTiles(int placed)
        => placed == 0
            ? 0
            : FlightMs + (placed - 1) * StaggerMs        // act 1 — the play
              + RoomMs                                   // act 2 — the board makes room
              + DropMs + (placed - 1) * DropStaggerMs    // act 3 — the tiles drop in
              + BufferMs;

    /// <summary>
    /// Reading-order indices (on <paramref name="next"/>, runs then groups) of the tiles that
    /// entered the board between the two renders — i.e. the tiles the client flies in from the
    /// hand. Uses the same set pairing and per-occurrence multiset diff as the client, so when a
    /// played tile has an identical twin already sitting on the board, the NEW copy is flagged,
    /// not the old one. Drives the played-tiles highlight.
    /// </summary>
    public static List<int> EnteredIndices(Solution previous, Solution next)
    {
        var prevSets = ReadingOrder(previous);
        var nextSets = ReadingOrder(next);
        var pairs = PairSets(prevSets, nextSets);

        var entered = new List<int>();
        var index = 0;
        for (var j = 0; j < nextSets.Count; j++)
        {
            // Tiles already in the paired previous set are consumed by occurrence; the rest
            // (and every tile of a brand-new set) entered the board.
            var counts = new Dictionary<int, int>();
            if (pairs[j].PrevIndex >= 0)
            {
                foreach (var tile in prevSets[pairs[j].PrevIndex].Tiles)
                {
                    var key = TileKey(tile);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }

            foreach (var tile in nextSets[j].Tiles)
            {
                var key = TileKey(tile);
                if (counts.TryGetValue(key, out var count) && count > 0) counts[key] = count - 1;
                else entered.Add(index);
                index++;
            }
        }

        return entered;
    }

    /// <summary>
    /// Reading-order indices (runs, then groups) of sets whose tiles changed between the two
    /// boards: a tile was added or removed, or the set is new. Sets that merely kept their
    /// seat are absent, so the UI can outline what the move actually touched.
    /// </summary>
    public static List<int> ModifiedSetIndices(Solution previous, Solution next)
    {
        var prevSets = ReadingOrder(previous);
        var nextSets = ReadingOrder(next);
        var pairs = PairSets(prevSets, nextSets);

        var modified = new List<int>();
        for (var j = 0; j < nextSets.Count; j++)
        {
            var prevIndex = pairs[j].PrevIndex;
            if (prevIndex < 0
                || pairs[j].Shared != prevSets[prevIndex].Tiles.Length
                || pairs[j].Shared != nextSets[j].Tiles.Length)
                modified.Add(j);
        }

        return modified;
    }

    // Tiles the client PLACES (acts 1 and 3): every tile of the next board its paired previous
    // set doesn't keep — tiles entering from the hand, plus board tiles changing set. Counted one
    // by one; the client flies tiles changing set together as one block, so this can only
    // over-estimate.
    private static int CountPlacedTiles(Solution previous, Solution next)
    {
        var nextSets = ReadingOrder(next);
        var pairs = PairSets(ReadingOrder(previous), nextSets);

        var placed = 0;
        for (var j = 0; j < nextSets.Count; j++)
            placed += nextSets[j].Tiles.Length - pairs[j].Shared;

        return placed;
    }

    // Pairs each next set with a previous set by maximal multiset overlap, greedy in reading
    // order (like BoardAlignment). Unpaired next sets get (-1, 0).
    private static (int PrevIndex, int Shared)[] PairSets(List<ValidSet> prevSets, List<ValidSet> nextSets)
    {
        var pairs = new (int PrevIndex, int Shared)[nextSets.Count];
        Array.Fill(pairs, (-1, 0));

        for (var i = 0; i < prevSets.Count; i++)
        {
            var best = -1;
            var bestShared = 0;
            for (var j = 0; j < nextSets.Count; j++)
            {
                if (pairs[j].PrevIndex >= 0) continue;
                var shared = SharedTileCount(prevSets[i], nextSets[j]);
                if (shared > bestShared) (best, bestShared) = (j, shared);
            }

            if (best >= 0) pairs[best] = (i, bestShared);
        }

        return pairs;
    }

    private static List<ValidSet> ReadingOrder(Solution board) => [..board.Runs, ..board.Groups];

    // Multiset intersection by tile identity, occurrence-aware; all jokers fold into one bucket.
    private static int SharedTileCount(ValidSet a, ValidSet b)
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
