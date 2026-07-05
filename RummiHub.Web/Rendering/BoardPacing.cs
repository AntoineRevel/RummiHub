using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Single source of truth for the sequential tile animation's tempo. The constants are pushed
/// to the JS module (<c>wwwroot/js/tile-animations.js</c>) via its <c>configure()</c> export,
/// and <see cref="EstimateMs"/> mirrors the client's step grouping so AI pacing
/// (<see cref="Models.Game.PendingAnimationMs"/>) never plays a move mid-animation.
/// </summary>
public static class BoardPacing
{
    /// <summary>One step's glide — a tile, or a whole relocated set gliding as a block.</summary>
    public const int MoveMs = 350;

    /// <summary>One entering tile's pop-in.</summary>
    public const int PopMs = 300;

    /// <summary>
    /// THE tempo dial: interval between successive step starts. Equal to <see cref="MoveMs"/>
    /// each step ends exactly when the next begins — strict one-at-a-time, no duration cap.
    /// (~100 would overlap the steps into a cascade.)
    /// </summary>
    public const int StartIntervalMs = MoveMs;

    /// <summary>Slack absorbing client jitter and the ±1 step the estimate can be off by.</summary>
    private const int BufferMs = 200;

    /// <summary>
    /// Estimated client animation duration (ms) for <paramref name="previous"/> →
    /// <paramref name="next"/>, which must already be aligned (<see cref="BoardAlignment"/>)
    /// so unchanged sets keep their reading-order index.
    /// </summary>
    public static int EstimateMs(Solution previous, Solution next)
        => DurationForSteps(CountSteps(previous, next));

    public static int DurationForSteps(int steps)
        => steps == 0 ? 0 : steps * StartIntervalMs + BufferMs;

    /// <summary>
    /// Reading-order indices (on <paramref name="next"/>, runs then groups) of the tiles that
    /// entered the board between the two renders — i.e. the tiles the client pops or flies in.
    /// Uses the same set pairing and per-occurrence multiset diff as the client, so when a
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
                foreach (var tile in prevSets[pairs[j].PrevIndex].Set.Tiles)
                {
                    var key = TileKey(tile);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }

            foreach (var tile in nextSets[j].Set.Tiles)
            {
                var key = TileKey(tile);
                if (counts.TryGetValue(key, out var count) && count > 0) counts[key] = count - 1;
                else entered.Add(index);
                index++;
            }
        }

        return entered;
    }

    // Counts animation steps the way the client groups them: a paired set that changed
    // reading-order index glides as ONE block step, plus one step per tile that entered it;
    // tiles of unpaired new sets are one step each. An estimate, not an exact replay —
    // BufferMs and the AI's watchable pause absorb the difference.
    private static int CountSteps(Solution previous, Solution next)
    {
        var prevSets = ReadingOrder(previous);
        var nextSets = ReadingOrder(next);
        var pairs = PairSets(prevSets, nextSets);

        var steps = 0;
        for (var j = 0; j < nextSets.Count; j++)
        {
            var (prevIndex, shared) = pairs[j];
            var target = nextSets[j];
            if (prevIndex < 0)
            {
                steps += target.Set.Tiles.Length;              // brand-new set: tile by tile
                continue;
            }

            var prev = prevSets[prevIndex];
            if (target.IsRun != prev.IsRun || target.Index != prev.Index)
                steps++;                                       // the kept tiles glide as one block
            steps += target.Set.Tiles.Length - shared;         // each entering tile flies/pops solo
        }

        return steps;
    }

    // Pairs each next set with a previous set by maximal multiset overlap, greedy in reading
    // order (like BoardAlignment). Unpaired next sets get (-1, 0).
    private static (int PrevIndex, int Shared)[] PairSets(
        List<(ValidSet Set, bool IsRun, int Index)> prevSets,
        List<(ValidSet Set, bool IsRun, int Index)> nextSets)
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
                var shared = SharedTileCount(prevSets[i].Set, nextSets[j].Set);
                if (shared > bestShared) (best, bestShared) = (j, shared);
            }

            if (best >= 0) pairs[best] = (i, bestShared);
        }

        return pairs;
    }

    private static List<(ValidSet Set, bool IsRun, int Index)> ReadingOrder(Solution board) =>
    [
        ..board.Runs.Select((s, i) => (s, true, i)),
        ..board.Groups.Select((s, i) => (s, false, i))
    ];

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
