using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Single source of truth for the tile animation's tempo. The constants are pushed to the JS
/// module (<c>wwwroot/js/tile-animations.js</c>) via its <c>configure()</c> export, and
/// <see cref="EstimateMs"/> estimates the client's board story so AI pacing
/// (<see cref="Models.Game.PendingAnimationMs"/>) waits for it. It is an estimate, not a bound:
/// how many blocks the board shifts depends on the client's layout (line wrapping); a board
/// arriving early waits on the client until the running story is over.
/// </summary>
/// <remarks>
/// A tile at rest never covers another, and only one thing moves at a time: a tile, or a block
/// of tiles stuck together (a set, the tail of a row). A board change is told step by step, one
/// step per group of sets that change together (a set the move builds or touches, plus the sets
/// tiles pass between with it), in reading order. In each step the board blocks move one after
/// the other — sliding along their row, or rising, travelling in the lane between rows and
/// dropping in — then the step's played tiles fly in one after the other; a set made only of
/// played tiles flies in whole, at once.
/// </remarks>
public static class BoardPacing
{
    /// <summary>A hand tile gliding between the rack and the play zone.</summary>
    public const int MoveMs = 350;

    /// <summary>A newcomer with no origin on screen popping in.</summary>
    public const int PopMs = 300;

    /// <summary>A board block rising out of its row.</summary>
    public const int LiftMs = 300;

    /// <summary>A board block sliding along its row, or travelling in the lane between rows.</summary>
    public const int SlideMs = 500;

    /// <summary>A lifted block dropping into its spot.</summary>
    public const int DropMs = 400;

    /// <summary>A played tile flying into its gap.</summary>
    public const int FlightMs = 700;

    /// <summary>Beat between two moves.</summary>
    public const int BeatMs = 150;

    /// <summary>Beat between two steps.</summary>
    public const int SetGapMs = 300;

    /// <summary>Most board blocks counted for making room for one set.</summary>
    public const int MaxRoomMoves = 8;

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
        => DurationFor(PlacedSets(previous, next));

    /// <summary>
    /// A set of the next board that gains tiles: how many come from the hand
    /// (<paramref name="Played"/>), how many from other sets (<paramref name="Moved"/>), how
    /// many sets, from this one to the end of the board, may shift to make room for them
    /// (<paramref name="Shifting"/>) — none for a new set of played tiles laid at the end —
    /// and whether it is a new set made only of played tiles, which flies in whole
    /// (<paramref name="Whole"/>).
    /// </summary>
    public readonly record struct PlacedSet(int Played, int Moved, int Shifting, bool Whole = false);

    // Every set of the next board gaining tiles its paired previous set doesn't have, in reading
    // order. A tile the previous board lacks came from the hand; any other one changes set (with
    // duplicates, either copy may count as such).
    private static List<PlacedSet> PlacedSets(Solution previous, Solution next)
    {
        var prevSets = previous.Sets;
        var nextSets = next.Sets;
        var pairs = BoardSeating.Pair(prevSets, nextSets);

        var fromHand = Counts(nextSets.SelectMany(set => set.Tiles));
        foreach (var tile in prevSets.SelectMany(set => set.Tiles))
            fromHand[TileKey(tile)] = fromHand.GetValueOrDefault(TileKey(tile)) - 1;

        var placedSets = new List<PlacedSet>();
        for (var j = 0; j < nextSets.Count; j++)
        {
            var kept = pairs[j].PrevIndex >= 0 ? Counts(prevSets[pairs[j].PrevIndex].Tiles) : [];
            int played = 0, moved = 0;
            foreach (var key in nextSets[j].Tiles.Select(TileKey))
            {
                if (kept.GetValueOrDefault(key) > 0) kept[key]--;
                else if (fromHand.GetValueOrDefault(key) > 0) { fromHand[key]--; played++; }
                else moved++;
            }
            if (played + moved == 0) continue;

            var laidAtTheEnd = moved == 0 && pairs.Skip(j).All(pair => pair.PrevIndex < 0);
            var whole = moved == 0 && pairs[j].PrevIndex < 0;
            placedSets.Add(new PlacedSet(played, moved, laidAtTheEnd ? 0 : nextSets.Count - j, whole));
        }

        return placedSets;
    }

    /// <summary>
    /// Estimated duration of the board story placing <paramref name="sets"/>, in reading order:
    /// each set as its own step (the client may group several); each tile changing set carried
    /// on its own (it may carry several together); room made by one block, plus one for every
    /// two shifting sets (a row's tail, a set wrapping to the next row), at most
    /// <see cref="MaxRoomMoves"/>, each counted as carried (sliding is shorter); a whole set of
    /// played tiles as one flight.
    /// </summary>
    public static int DurationFor(IReadOnlyList<PlacedSet> sets)
    {
        const int carry = LiftMs + SlideMs + DropMs + BeatMs;
        return sets.Count == 0
            ? 0
            : sets.Sum(set => set.Moved * carry
                              + (set.Shifting > 0 ? Math.Min(MaxRoomMoves, 1 + set.Shifting / 2) * carry : 0)
                              + (set.Whole ? 1 : set.Played) * (FlightMs + BeatMs))
              + (sets.Count - 1) * SetGapMs
              + BufferMs;
    }

    private static Dictionary<int, int> Counts(IEnumerable<Tile> tiles)
    {
        var counts = new Dictionary<int, int>();
        foreach (var tile in tiles) counts[TileKey(tile)] = counts.GetValueOrDefault(TileKey(tile)) + 1;
        return counts;
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
