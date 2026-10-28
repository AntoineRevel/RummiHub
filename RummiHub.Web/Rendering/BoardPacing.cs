using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Single source of truth for the tile animation's tempo. The constants are pushed to the JS
/// module (<c>wwwroot/js/tile-animations.js</c>) via its <c>configure()</c> export, and
/// <see cref="EstimateMs"/> mirrors the client's board story so AI pacing
/// (<see cref="Models.Game.PendingAnimationMs"/>) never plays a move mid-animation.
/// </summary>
/// <remarks>
/// A tile at rest never covers another. A board change is told step by step, one step per group
/// of sets that change together (a set the move builds or touches, plus the sets tiles pass
/// between with it), in reading order. Each step is told in three acts, on its sets only:
/// (1) its board tiles changing set rise in place; (2) in ONE glide the board makes room for
/// this step alone while they travel to hover just above their new gap; (3) its tiles come in
/// one after the other — played tiles fly out of the play zone (or the mover's chip), hovering
/// ones drop. Then the next step. A beat follows acts 1 and 2; a step with nothing to lift or
/// make room for is act 3 alone.
/// </remarks>
public static class BoardPacing
{
    /// <summary>A hand tile gliding between the rack and the play zone.</summary>
    public const int MoveMs = 350;

    /// <summary>A newcomer with no origin on screen popping in.</summary>
    public const int PopMs = 300;

    /// <summary>Act 1 — a step's board tiles changing set rising in place.</summary>
    public const int LiftMs = 400;

    /// <summary>Beat after acts 1 and 2, so each reads on its own.</summary>
    public const int PauseMs = 300;

    /// <summary>Act 2 — the board making room for one step as its lifted tiles travel above their gaps.</summary>
    public const int RoomMs = 900;

    /// <summary>Act 3 — one hovering tile dropping into its gap.</summary>
    public const int DropMs = 600;

    /// <summary>Act 3 — a played tile flying into its gap.</summary>
    public const int FlightMs = 900;

    /// <summary>Act 3 — interval between two tiles of a step.</summary>
    public const int TileGapMs = 200;

    /// <summary>Beat between two sets of a step, and between two steps.</summary>
    public const int SetGapMs = 300;

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
    /// A set of the next board that gains tiles: how many (<paramref name="Tiles"/>), whether
    /// some come from other sets — they rise first (<paramref name="ChangesSet"/>) — and whether
    /// the board has to make room for it (<paramref name="MakesRoom"/>).
    /// </summary>
    public readonly record struct PlacedSet(int Tiles, bool ChangesSet, bool MakesRoom);

    // Every set of the next board gaining tiles its paired previous set doesn't have, in reading
    // order. A tile the previous board lacks came from the hand; any other one changes set (with
    // duplicates, either copy may count as such). Only a new set of played tiles with nothing
    // but new sets after it lands without moving anything.
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
            var placed = 0;
            var changesSet = false;
            foreach (var key in nextSets[j].Tiles.Select(TileKey))
            {
                if (kept.GetValueOrDefault(key) > 0) { kept[key]--; continue; }
                placed++;
                if (fromHand.GetValueOrDefault(key) > 0) fromHand[key]--;
                else changesSet = true;
            }
            if (placed == 0) continue;

            var appended = !changesSet && pairs.Skip(j).All(pair => pair.PrevIndex < 0);
            placedSets.Add(new PlacedSet(placed, changesSet, !appended));
        }

        return placedSets;
    }

    /// <summary>
    /// Duration of the board story placing <paramref name="sets"/>, in reading order.
    /// Conservative: each set counts as its own step (the client may group several), every tile
    /// as flying in (a drop is shorter), and room for any set but a new one laid at the end,
    /// since whether the board really reflows depends on the client's layout (line wrapping).
    /// </summary>
    public static int DurationFor(IReadOnlyList<PlacedSet> sets)
        => sets.Count == 0
            ? 0
            : sets.Sum(set => (set.ChangesSet ? LiftMs + PauseMs : 0)           // act 1 — tiles changing set rise
                              + (set.MakesRoom ? RoomMs + PauseMs : 0)          // act 2 — the board makes room
                              + (set.Tiles - 1) * TileGapMs + FlightMs)         // act 3 — its tiles come in
              + (sets.Count - 1) * SetGapMs
              + BufferMs;

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
