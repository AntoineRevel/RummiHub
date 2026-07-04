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
    /// THE tempo dial: interval between successive step starts. Equal to <see cref="MoveMs"/>,
    /// each step finishes exactly when the next one begins — strict one-at-a-time, no duration
    /// cap however many steps there are. (~100 would overlap the steps into a cascade.)
    /// </summary>
    public const int StartIntervalMs = MoveMs;

    /// <summary>Slack absorbing client jitter and the ±1 step the estimate can be off by.</summary>
    private const int BufferMs = 200;

    /// <summary>
    /// Estimated duration (ms) of the client animation for <paramref name="previous"/> →
    /// <paramref name="next"/>. Expects <paramref name="next"/> to already be aligned
    /// (see <see cref="BoardAlignment"/>) so unchanged sets keep their reading-order index.
    /// </summary>
    public static int EstimateMs(Solution previous, Solution next)
        => DurationForSteps(CountSteps(previous, next));

    public static int DurationForSteps(int steps)
        => steps == 0 ? 0 : steps * StartIntervalMs + BufferMs;

    /// <summary>
    /// Counts animation steps the way the client groups them: sets are paired prev↔next by
    /// maximal multiset overlap (greedy in reading order, like <see cref="BoardAlignment"/>);
    /// a paired set that changed reading-order index glides as ONE block step, plus one step
    /// per tile that entered it; tiles of unpaired new sets are one step each. An estimate,
    /// not an exact replay — <see cref="BufferMs"/> and the AI's watchable pause absorb the
    /// difference.
    /// </summary>
    private static int CountSteps(Solution previous, Solution next)
    {
        var prevSets = ReadingOrder(previous);
        var nextSets = ReadingOrder(next);
        var paired = new bool[nextSets.Count];
        var steps = 0;

        foreach (var prev in prevSets)
        {
            var best = -1;
            var bestShared = 0;
            for (var j = 0; j < nextSets.Count; j++)
            {
                if (paired[j]) continue;
                var shared = SharedTileCount(prev.Set, nextSets[j].Set);
                if (shared > bestShared) (best, bestShared) = (j, shared);
            }

            if (best < 0) continue;
            paired[best] = true;

            var target = nextSets[best];
            if (target.IsRun != prev.IsRun || target.Index != prev.Index)
                steps++;                                       // the kept tiles glide as one block
            steps += target.Set.Tiles.Length - bestShared;     // each entering tile flies/pops solo
        }

        for (var j = 0; j < nextSets.Count; j++)
            if (!paired[j]) steps += nextSets[j].Set.Tiles.Length;   // brand-new set: tile by tile

        return steps;
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
