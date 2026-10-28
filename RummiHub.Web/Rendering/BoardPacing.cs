using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Single source of truth for the tile animation's tempo. The constants are pushed to the JS
/// module (<c>wwwroot/js/tile-animations.js</c>) via its <c>configure()</c> export, and
/// <see cref="EstimateMs"/> mirrors the client's board story so AI pacing
/// (<see cref="Models.Game.PendingAnimationMs"/>) never plays a move mid-animation.
/// </summary>
/// <remarks>
/// A tile at rest never covers another. Every board change is told in three acts: (1) board
/// tiles changing set rise in place; (2) in ONE glide the board makes room while they travel to
/// hover just above their new gap; (3) set by set, in reading order, the tiles joining that set
/// come in one after the other — played tiles fly out of the play zone (or the mover's chip),
/// hovering ones drop — then the next set. A beat follows acts 1 and 2. With no room to make,
/// act 3 plays alone.
/// </remarks>
public static class BoardPacing
{
    /// <summary>A hand tile gliding between the rack and the play zone.</summary>
    public const int MoveMs = 350;

    /// <summary>A newcomer with no origin on screen popping in.</summary>
    public const int PopMs = 300;

    /// <summary>Act 1 — board tiles changing set rising in place.</summary>
    public const int LiftMs = 500;

    /// <summary>Beat after acts 1 and 2, so each reads on its own.</summary>
    public const int PauseMs = 450;

    /// <summary>Act 2 — the board making room as the lifted tiles travel above their gaps.</summary>
    public const int RoomMs = 1200;

    /// <summary>Act 3 — one hovering tile dropping into its gap.</summary>
    public const int DropMs = 650;

    /// <summary>Act 3 — a played tile flying into its gap.</summary>
    public const int FlightMs = 1200;

    /// <summary>Act 3 — interval between two tiles joining the same set.</summary>
    public const int TileGapMs = 200;

    /// <summary>Act 3 — beat once a set is complete, before the next set's tiles come.</summary>
    public const int SetGapMs = 300;

    /// <summary>Act 3 — longest spread from the first set to the last: many sets come closer together.</summary>
    public const int MaxSpreadMs = 6000;

    /// <summary>Slack absorbing client jitter.</summary>
    private const int BufferMs = 200;

    /// <summary>
    /// Reading window once a move has landed, before an AI seat may play: time to see which
    /// tiles came in and which sets changed. Grows with each further set the move touched.
    /// </summary>
    public const int ReadMs = 2500;

    /// <summary>Extra reading time per set the move touched beyond the first.</summary>
    public const int ReadPerSetMs = 500;

    /// <summary>Longest reading window, however much the move rearranged.</summary>
    public const int MaxReadMs = 5000;

    /// <summary>
    /// Estimated client animation duration (ms) for <paramref name="previous"/> →
    /// <paramref name="next"/>, which must already be aligned (<see cref="BoardAlignment"/>)
    /// so unchanged sets keep their reading-order index.
    /// </summary>
    public static int EstimateMs(Solution previous, Solution next)
        => DurationForPlacedTiles(PlacedPerSet(previous, next));

    // Tiles the client PLACES (act 3), per set of the next board in reading order: every tile
    // its paired previous set doesn't keep — tiles entering from the hand, plus board tiles
    // changing set. Counted one by one; the client brings tiles changing set together as one
    // block, so this can only over-estimate.
    private static List<int> PlacedPerSet(Solution previous, Solution next)
    {
        var pairs = BoardSeating.Pair(previous.Sets, next.Sets);
        return next.Sets.Select((set, j) => set.Tiles.Length - pairs[j].Shared).ToList();
    }

    /// <summary>
    /// Duration of the board story placing <paramref name="placedPerSet"/> tiles in each set, in
    /// reading order (sets placing none are skipped). Conservative: it always counts acts 1 and 2,
    /// since whether the board really has to make room depends on the client's layout (line
    /// wrapping), unknown here; and every tile as flying in, which outlasts a drop.
    /// </summary>
    public static int DurationForPlacedTiles(IEnumerable<int> placedPerSet)
    {
        var placed = PlaceMs(placedPerSet.Where(count => count > 0).ToList());
        return placed == 0
            ? 0
            : LiftMs + PauseMs      // act 1 — tiles changing set rise
              + RoomMs + PauseMs    // act 2 — the board makes room
              + placed              // act 3 — set by set, the tiles come in
              + BufferMs;
    }

    // Act 3, like the client's schedule(): each set's tiles come TileGapMs apart, the next set
    // waits for the last of them plus SetGapMs; many sets are squeezed so the last one starts
    // within MaxSpreadMs.
    private static int PlaceMs(List<int> counts)
    {
        if (counts.Count == 0) return 0;
        var lengths = counts.Select(count => (count - 1) * TileGapMs + FlightMs).ToList();
        var starts = new double[counts.Count];
        for (var k = 1; k < counts.Count; k++) starts[k] = starts[k - 1] + lengths[k - 1] + SetGapMs;
        var squeeze = starts[^1] > MaxSpreadMs ? MaxSpreadMs / starts[^1] : 1;
        return (int)Math.Round(starts.Select((start, k) => start * squeeze + lengths[k]).Max());
    }

    /// <summary>
    /// Reading window (ms) after the <paramref name="previous"/> → <paramref name="next"/>
    /// animation: 0 when no set changed (a draw), else <see cref="ReadMs"/> plus
    /// <see cref="ReadPerSetMs"/> per further set touched, capped at <see cref="MaxReadMs"/>.
    /// Both boards must already be aligned, like <see cref="EstimateMs"/>.
    /// </summary>
    public static int ReadingMs(Solution previous, Solution next)
    {
        var touched = ModifiedSetIndices(previous, next).Count;
        return touched == 0 ? 0 : Math.Min(ReadMs + (touched - 1) * ReadPerSetMs, MaxReadMs);
    }

    /// <summary>
    /// True when both boards hold the same sets with the same tiles in the same order, i.e.
    /// the client has nothing to redraw (a draw, a turn confirmation).
    /// </summary>
    public static bool SameBoard(Solution previous, Solution next) =>
        SameSets(previous.Sets, next.Sets);

    private static bool SameSets(List<ValidSet> previous, List<ValidSet> next) =>
        previous.Count == next.Count
        && previous.Zip(next).All(pair => pair.First.Tiles.AsSpan().SequenceEqual(pair.Second.Tiles));

    /// <summary>
    /// Reading-order indices (on <paramref name="next"/>) of the tiles that
    /// entered the board between the two renders — i.e. the tiles the client flies in from the
    /// hand. Uses the same set pairing and per-occurrence multiset diff as the client, so when a
    /// played tile has an identical twin already sitting on the board, the NEW copy is flagged,
    /// not the old one. Drives the played-tiles highlight.
    /// </summary>
    public static List<int> EnteredIndices(Solution previous, Solution next)
    {
        var prevSets = previous.Sets;
        var nextSets = next.Sets;
        var pairs = BoardSeating.Pair(prevSets, nextSets);

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
    /// The <see cref="EnteredIndices"/> that hold a <paramref name="played"/> tile: what came
    /// out of the mover's rack, without the board tiles that merely changed set (those show
    /// through <see cref="ModifiedSetIndices"/>). Where a played tile and an identical board
    /// tile both changed set, either copy may light up — they look the same.
    /// </summary>
    public static List<int> PlayedIndices(Solution previous, Solution next, IEnumerable<Tile> played)
    {
        var remaining = new Dictionary<int, int>();
        foreach (var tile in played)
        {
            var key = TileKey(tile);
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        var tiles = next.Sets.SelectMany(set => set.Tiles).ToArray();
        var result = new List<int>();
        foreach (var index in EnteredIndices(previous, next))
        {
            var key = TileKey(tiles[index]);
            if (!remaining.TryGetValue(key, out var count) || count == 0) continue;
            remaining[key] = count - 1;
            result.Add(index);
        }

        return result;
    }

    /// <summary>
    /// Reading-order indices of sets whose tiles changed between the two
    /// boards: a tile was added or removed, or the set is new. Sets that merely kept their
    /// seat are absent, so the UI can outline what the move actually touched.
    /// </summary>
    public static List<int> ModifiedSetIndices(Solution previous, Solution next)
    {
        var prevSets = previous.Sets;
        var nextSets = next.Sets;
        var pairs = BoardSeating.Pair(prevSets, nextSets);

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

    private static int TileKey(Tile tile) => tile.IsJoker ? -1 : (tile.Value << 2) | (int)tile.Color;
}
