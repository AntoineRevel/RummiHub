using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.Solver.Worker.Solver.Dynamic;

/// <summary>
/// Post-processes a during-game solve so the optimal move disturbs the table as little as
/// possible. Solvers rebuild the whole board from scratch, so even "add one tile to a group"
/// comes back as a fully reshuffled table; players read that as chaos.
///
/// <para>
/// The tiles played stay exactly the ones the solver chose (same optimality). What changes
/// is only the arrangement: <see cref="BoardRecomposer"/> finds the one moving the fewest
/// tiles, then the fewest sets, the way a player would do it by hand. Runs and groups are
/// one list: a run may come back as groups and the other way round. Every set the move
/// leaves alone is returned as the same instance, a set that keeps tiles keeps them in the
/// order they were in, and every rebuilt set sits beside the set its tiles came from
/// (<see cref="BoardSeating"/>).
/// </para>
/// <para>
/// A move that reworks so much of the table that the search runs out of budget gets the
/// best arrangement it came across, or the solver's own sets when those move fewer tiles,
/// laid out the same way.
/// </para>
/// </summary>
public static class BoardStabilizer
{
    /// <summary>
    /// Sets the exact search may try, a fifth of a microsecond each: 20 ms at worst. Nine
    /// moves out of ten need under a tenth of it.
    /// </summary>
    private const int MaxWork = 100_000;

    /// <summary>Sets the last pass may try once the exact search has run out.</summary>
    private const int LastPassWork = 30_000;

    public static SolverResult Stabilize(Solution previousBoard, SolverResult solved,
        CancellationToken cancellationToken = default)
    {
        if (!solved.Found || cancellationToken.IsCancellationRequested) return solved;

        var played = PlayedTiles(solved);
        if (played.Count == 0) return solved;

        var previous = previousBoard.Sets;
        if (previous.Count == 0) return solved;

        var sets = BoardRecomposer.Recompose(previous, played, MaxWork, LastPassWork, out var cheapest,
            cancellationToken);
        var stabilized = sets is null ? null : Finish(solved, previousBoard, played, LayOut(previous, sets));
        if (stabilized is not null && cheapest) return stabilized;

        // Past its budget the search only returns the best arrangement it came across: the
        // solver's own sets may still move fewer tiles.
        var solverSets = solved.BestSolution.Sets.Select(s => (IReadOnlyList<Tile>)s.Tiles).ToList();
        var fallback = Finish(solved, previousBoard, played, LayOut(previous, solverSets)) ?? solved;
        return stabilized is not null && Moved(stabilized) <= Moved(fallback) ? stabilized : fallback;

        int Moved(SolverResult result) => BoardSeating.MovedTiles(previous, result.BestSolution.Sets);
    }

    /// <summary>
    /// Lays <paramref name="sets"/> (tiles only, in any order) on the previous board: a set
    /// that already existed comes back as the original instance, any other is written after
    /// the previous set it lives on from (<see cref="LaidLike"/>), and all are then ordered
    /// by <see cref="BoardSeating.Seat"/>.
    /// </summary>
    private static Solution LayOut(List<ValidSet> previous, IReadOnlyList<IReadOnlyList<Tile>> sets)
    {
        var laid = sets.Select(tiles => new ValidSet(tiles.ToArray())).ToList();
        var pairs = BoardSeating.Pair(previous, laid);
        for (var j = 0; j < laid.Count; j++)
        {
            var home = pairs[j].PrevIndex >= 0 ? previous[pairs[j].PrevIndex] : null;
            var kept = home is not null && pairs[j].Shared == home.Tiles.Length
                                        && pairs[j].Shared == laid[j].Tiles.Length;
            laid[j] = kept ? home! : LaidLike(home, laid[j].Tiles) ?? laid[j];
        }

        return new Solution { IsValid = true, Sets = BoardSeating.Seat(previous, laid) };
    }

    /// <summary>
    /// The valid set made of <paramref name="tiles"/>, written so the tiles it keeps from
    /// <paramref name="home"/> do not move: a group keeps them in their seats and appends
    /// the newcomers, a run keeps a spare joker on the end it was on. Jokers take whatever
    /// tile the set needs them to stand for.
    /// </summary>
    private static ValidSet? LaidLike(ValidSet? home, IReadOnlyList<Tile> tiles)
    {
        var reals = tiles.Where(t => !t.IsJoker).ToList();
        var jokers = tiles.Count - reals.Count;
        if (reals.Count == 0) return null;

        var isGroup = reals.Count > 1
            ? reals.All(t => t.Value == reals[0].Value)
            : home?.IsGroup ?? true;
        return isGroup ? GroupLike(home, reals, jokers) : RunLike(home, reals, jokers);
    }

    private static ValidSet? RunLike(ValidSet? home, List<Tile> reals, int jokers)
    {
        var color = reals[0].Color;
        var ordered = reals.OrderBy(t => t.Value).ToList();
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].Color != color || (i > 0 && ordered[i].Value == ordered[i - 1].Value))
                return null;

        var low = ordered[0].Value;
        var high = ordered[^1].Value;
        var spare = jokers - (high - low + 1 - ordered.Count);
        if (spare < 0 || ordered.Count + jokers < 3) return null;

        // Spare jokers extend the high end, so the run reads as an append, unless the run
        // they were on already had them in front.
        var wereInFront = home is { IsGroup: false } ? home.Tiles.TakeWhile(t => t.IsJoker).Count() : 0;
        var left = Math.Min(Math.Min(spare, wereInFront), low - 1);
        var right = Math.Min(spare - left, 13 - high);
        left = spare - right;
        if (left > low - 1) return null;

        var tiles = new Tile[high - low + 1 + spare];
        var next = 0;
        for (var value = low - left; value <= high + right; value++)
        {
            tiles[value - low + left] = next < ordered.Count && ordered[next].Value == value
                ? ordered[next++]
                : new Tile(value, color, isJoker: true);
        }

        return new ValidSet(tiles);
    }

    private static ValidSet? GroupLike(ValidSet? home, List<Tile> reals, int jokers)
    {
        var count = reals.Count + jokers;
        var value = reals[0].Value;
        if (count is < 3 or > 4 || reals.Any(t => t.Value != value)
                                || reals.Select(t => t.Color).Distinct().Count() != reals.Count)
            return null;

        // Tiles that were already together keep their seats. A joker gives its seat to the
        // real tile it stood for when that one joins; else it keeps its color if no real
        // tile of the group has it.
        var free = Enum.GetValues<TileColor>().Except(reals.Select(t => t.Color)).ToList();
        var pending = reals.ToList();
        var jokersLeft = jokers;
        var tiles = new List<Tile>(count);
        foreach (var tile in home?.Tiles ?? [])
        {
            var stoodFor = home!.IsGroup ? pending.FindIndex(t => t.Color == tile.Color) : -1;
            if (!tile.IsJoker || stoodFor >= 0)
            {
                var real = tile.IsJoker ? pending[stoodFor] : tile;
                if (pending.Remove(real)) tiles.Add(real);
                continue;
            }

            if (jokersLeft == 0) continue;
            jokersLeft--;
            var color = home.IsGroup && free.Contains(tile.Color) ? tile.Color : free[0];
            free.Remove(color);
            tiles.Add(new Tile(value, color, isJoker: true));
        }

        tiles.AddRange(pending);
        for (; jokersLeft > 0; jokersLeft--)
        {
            tiles.Add(new Tile(value, free[0], isJoker: true));
            free.RemoveAt(0);
        }

        return new ValidSet(tiles.ToArray());
    }

    private static SolverResult? Finish(SolverResult solved, Solution previousBoard, List<Tile> played,
        Solution arranged)
    {
        var score = arranged.Sets.Sum(s => s.GetScore());
        var jokers = played.Count(t => t.IsJoker);
        var reals = played.Where(t => !t.IsJoker).ToList();
        var result = SolverResult.FromSolution(solved.Source, arranged, reals, jokers, score);
        return IsLegal(result, previousBoard, reals, jokers) ? result : null;
    }

    private static bool IsLegal(SolverResult result, Solution previousBoard, List<Tile> playedReals, int playedJokers)
    {
        var rack = new Set(playedReals);
        for (var i = 0; i < playedJokers; i++)
            rack.AddTile(new Tile(isJoker: true));

        return SolutionValidator.IsValid(result, previousBoard.ToSet(), rack, hasPlayed: true);
    }

    private static List<Tile> PlayedTiles(SolverResult solved)
    {
        var played = solved.TilesToPlay.Where(t => !t.IsJoker).ToList();
        var jokers = solved.JokersToPlay + solved.TilesToPlay.Count(t => t.IsJoker);
        for (var i = 0; i < jokers; i++)
            played.Add(new Tile(isJoker: true));
        return played;
    }
}
