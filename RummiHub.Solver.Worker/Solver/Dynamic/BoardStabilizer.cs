using System.Diagnostics;
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
/// is only the arrangement, in three attempts, cheapest first:
/// </para>
/// <list type="number">
/// <item>Pure growth: each played tile extends an existing run or group, or the played tiles
/// form brand-new sets. Every other set is returned as the same instance, in its old seat,
/// and an extension keeps the tiles that were already there in their old order.</item>
/// <item>A bounded search over the sets that actually touch the played tiles. Combinations
/// are tried fewest-breaks first, most-relevant sets first, and recomposed locally. Sets
/// outside the combination are returned untouched.</item>
/// <item>When the move genuinely needs a wider rebuild than the budget allows, sets the
/// solver happened to recreate unchanged are put back in their old seat anyway, so a
/// fallback never reshuffles a set it did not need to touch.</item>
/// </list>
/// </summary>
public static class BoardStabilizer
{
    /// <summary>Played tiles above this skip the pure-growth pass (2^n gets uncomfortable).</summary>
    private const int FastPathMaxTiles = 12;

    /// <summary>How many of the most relevant sets the bounded search is willing to break.</summary>
    private const int MaxCandidates = 9;

    /// <summary>Breaks beyond this are left to the anchored solver arrangement.</summary>
    private const int MaxBrokenSets = 6;

    private const int MaxAttempts = 900;
    private static readonly TimeSpan MaxSearchTime = TimeSpan.FromMilliseconds(450);

    private const int Inf = 1_000_000;

    public static SolverResult Stabilize(Solution previousBoard, SolverResult solved,
        CancellationToken cancellationToken = default)
    {
        if (!solved.Found || cancellationToken.IsCancellationRequested) return solved;

        var played = PlayedTiles(solved);
        if (played.Count == 0) return solved;

        var previous = ListSets(previousBoard);
        if (previous.Count == 0) return solved;

        if (TryFastPath(previousBoard, previous, played) is { } grown)
        {
            var fast = Finish(solved, previousBoard, played, grown);
            if (fast is not null) return fast;
        }

        if (TrySearch(previousBoard, previous, solved, played, cancellationToken) is { } searched)
            return searched;

        return Anchor(previousBoard, previous, solved, played) ?? solved;
    }

    /// <summary>
    /// Extensions and self-contained new sets. Succeeds only when every played tile is
    /// consumed; the dynamic program picks the cover that breaks the fewest sets.
    /// </summary>
    private static Solution? TryFastPath(Solution previousBoard, List<(bool IsRun, ValidSet Set)> previous,
        List<Tile> played)
    {
        var n = played.Count;
        if (n > FastPathMaxTiles) return null;

        var full = (1 << n) - 1;
        var perSet = new List<(int Mask, ValidSet Built)>[previous.Count];
        for (var i = 0; i < previous.Count; i++)
        {
            perSet[i] = [];
            var relevant = RelevantBits(previous[i], played);
            for (var sub = relevant; sub > 0; sub = (sub - 1) & relevant)
            {
                var added = TilesFor(played, sub);
                var built = previous[i].IsRun
                    ? TryBuildRun(previous[i].Set.Tiles, added)
                    : TryBuildGroup(previous[i].Set.Tiles, added);
                if (built is not null) perSet[i].Add((sub, built));
            }
        }

        var fresh = new List<(int Mask, bool IsRun, ValidSet Built)>();
        for (var mask = 1; mask <= full; mask++)
        {
            var added = TilesFor(played, mask);
            if (TryBuildGroup([], added) is { } group) fresh.Add((mask, false, group));
            else if (TryBuildRun([], added) is { } run) fresh.Add((mask, true, run));
        }

        var rows = previous.Count + 1;
        var dp = new int[rows][];
        var parentMask = new int[rows][];
        var parentOption = new int[rows][];
        for (var row = 0; row < rows; row++)
        {
            dp[row] = new int[full + 1];
            parentMask[row] = new int[full + 1];
            parentOption[row] = new int[full + 1];
            Array.Fill(dp[row], Inf);
            Array.Fill(parentOption[row], -2);
        }

        dp[0][0] = 0;
        for (var i = 0; i < previous.Count; i++)
        {
            for (var mask = 0; mask <= full; mask++)
            {
                var cost = dp[i][mask];
                if (cost >= Inf) continue;

                if (cost < dp[i + 1][mask])
                {
                    dp[i + 1][mask] = cost;
                    parentMask[i + 1][mask] = mask;
                    parentOption[i + 1][mask] = -1;
                }

                for (var option = 0; option < perSet[i].Count; option++)
                {
                    var optMask = perSet[i][option].Mask;
                    if ((mask & optMask) != 0) continue;
                    var next = mask | optMask;
                    var nextCost = cost + 1;
                    if (nextCost < dp[i + 1][next])
                    {
                        dp[i + 1][next] = nextCost;
                        parentMask[i + 1][next] = mask;
                        parentOption[i + 1][next] = option;
                    }
                }
            }
        }

        var best = (int[])dp[previous.Count].Clone();
        var freshParentMask = new int[full + 1];
        var freshParentOption = new int[full + 1];
        Array.Fill(freshParentOption, -2);
        for (var mask = 0; mask <= full; mask++)
        {
            if (best[mask] >= Inf) continue;
            for (var option = 0; option < fresh.Count; option++)
            {
                var optMask = fresh[option].Mask;
                if ((mask & optMask) != 0) continue;
                var next = mask | optMask;
                if (best[mask] < best[next])
                {
                    best[next] = best[mask];
                    freshParentMask[next] = mask;
                    freshParentOption[next] = option;
                }
            }
        }

        if (best[full] >= Inf) return null;

        var newRuns = new List<ValidSet>();
        var newGroups = new List<ValidSet>();
        var remainder = full;
        while (freshParentOption[remainder] >= 0)
        {
            var option = fresh[freshParentOption[remainder]];
            if (option.IsRun) newRuns.Add(option.Built);
            else newGroups.Add(option.Built);
            remainder = freshParentMask[remainder];
        }

        var grownBySet = new ValidSet?[previous.Count];
        var rowCursor = previous.Count;
        var maskCursor = remainder;
        while (rowCursor > 0)
        {
            var option = parentOption[rowCursor][maskCursor];
            if (option == -2) return null;
            if (option >= 0)
                grownBySet[rowCursor - 1] = perSet[rowCursor - 1][option].Built;
            maskCursor = parentMask[rowCursor][maskCursor];
            rowCursor--;
        }

        var runs = previousBoard.Runs.ToList();
        var groups = previousBoard.Groups.ToList();
        var runCount = runs.Count;
        for (var i = 0; i < previous.Count; i++)
        {
            if (grownBySet[i] is not { } built) continue;
            if (previous[i].IsRun) runs[i] = built;
            else groups[i - runCount] = built;
        }

        runs.AddRange(newRuns);
        groups.AddRange(newGroups);
        return new Solution { IsValid = true, Runs = runs, Groups = groups };
    }

    private static SolverResult? TrySearch(Solution previousBoard, List<(bool IsRun, ValidSet Set)> previous,
        SolverResult solved, List<Tile> played, CancellationToken cancellationToken)
    {
        var rank = Relevance(previous, played);
        var candidates = Enumerable.Range(0, previous.Count)
            .Where(i => rank[i] > 0)
            .OrderByDescending(i => rank[i])
            .ThenBy(i => i)
            .Take(MaxCandidates)
            .ToArray();
        if (candidates.Length == 0) return null;

        var playedReals = played.Where(t => !t.IsJoker).ToList();
        var playedJokers = played.Count(t => t.IsJoker);
        var stopwatch = Stopwatch.StartNew();
        var attemptsLeft = MaxAttempts;
        var maxBroken = Math.Min(MaxBrokenSets, Math.Min(candidates.Length, previous.Count - 1));

        for (var brokenCount = 1; brokenCount <= maxBroken; brokenCount++)
        {
            foreach (var combo in Combinations(candidates, brokenCount))
            {
                if (--attemptsLeft < 0 || stopwatch.Elapsed > MaxSearchTime
                                       || cancellationToken.IsCancellationRequested)
                    return null;

                var recomposed = TryRecompose(previous, combo, playedReals, playedJokers, cancellationToken);
                if (recomposed is null) continue;

                var assembled = Assemble(previous, combo, recomposed);
                var laid = LayOut(previous, assembled);
                if (laid is null) continue;

                var result = Finish(solved, previousBoard, played, laid);
                if (result is not null) return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Puts sets the solver recreated unchanged back into the seat they already occupied.
    /// </summary>
    private static SolverResult? Anchor(Solution previousBoard, List<(bool IsRun, ValidSet Set)> previous,
        SolverResult solved, List<Tile> played)
    {
        var laid = LayOut(previous, solved.BestSolution);
        if (laid is null) return null;
        return Finish(solved, previousBoard, played, laid);
    }

    private static Solution? TryRecompose(List<(bool IsRun, ValidSet Set)> previous, int[] combo,
        List<Tile> playedTiles, int playedJokers, CancellationToken cancellationToken)
    {
        var broken = new Set();
        foreach (var index in combo)
            broken.Concat(previous[index].Set);

        var rack = new Set(playedTiles);
        for (var i = 0; i < playedJokers; i++)
            rack.AddTile(new Tile(isJoker: true));

        var sub = DynamicSolver.Create(broken, rack).SearchSolution(cancellationToken);
        if (!sub.Found) return null;
        if (sub.TilesToPlay.Count(t => !t.IsJoker) != playedTiles.Count || sub.JokersToPlay != playedJokers)
            return null;

        return sub.BestSolution;
    }

    private static Solution Assemble(List<(bool IsRun, ValidSet Set)> previous, int[] combo, Solution recomposed)
    {
        var broken = new HashSet<int>(combo);
        var solution = new Solution { IsValid = true };
        for (var i = 0; i < previous.Count; i++)
        {
            if (broken.Contains(i)) continue;
            if (previous[i].IsRun) solution.AddRun(previous[i].Set);
            else solution.AddGroup(previous[i].Set);
        }

        solution.Runs.AddRange(recomposed.Runs);
        solution.Groups.AddRange(recomposed.Groups);
        return solution;
    }

    /// <summary>
    /// Re-seats <paramref name="candidate"/> on the previous board: identical sets come back
    /// as the original instances, extensions keep the old tile order, and anything new fills
    /// the seat of the set it shares the most tiles with. Returns null when not a single
    /// previous set survived — there is nothing to anchor, and the solver arrangement stands.
    /// </summary>
    private static Solution? LayOut(List<(bool IsRun, ValidSet Set)> previous, Solution candidate)
    {
        var pool = new List<PoolSet>();
        foreach (var set in candidate.Runs) pool.Add(new PoolSet(true, set));
        foreach (var set in candidate.Groups) pool.Add(new PoolSet(false, set));

        var placed = new ValidSet?[previous.Count];
        var preserved = 0;
        for (var i = 0; i < previous.Count; i++)
        {
            var match = pool.FirstOrDefault(p =>
                !p.Used && p.IsRun == previous[i].IsRun && SameCounts(p.Set, previous[i].Set));
            if (match is null) continue;
            match.Used = true;
            placed[i] = previous[i].Set;
            preserved++;
        }

        if (preserved == 0) return null;

        var baseUsed = new bool[previous.Count];
        for (var i = 0; i < previous.Count; i++)
            baseUsed[i] = placed[i] is not null;

        foreach (var item in pool)
        {
            if (item.Used) continue;
            var best = -1;
            var bestSize = 0;
            for (var i = 0; i < previous.Count; i++)
            {
                if (baseUsed[i] || previous[i].IsRun != item.IsRun) continue;
                if (!ContainsCounts(item.Set, previous[i].Set)) continue;
                var size = previous[i].Set.Tiles.Length;
                if (size <= bestSize) continue;
                bestSize = size;
                best = i;
            }

            if (best < 0) continue;
            if (AdoptGrowth(previous[best].IsRun, previous[best].Set, item.Set) is not { } adopted) continue;
            item.Set = adopted;
            baseUsed[best] = true;
        }

        var runs = new List<ValidSet>();
        var groups = new List<ValidSet>();
        for (var i = 0; i < previous.Count; i++)
        {
            var dest = previous[i].IsRun ? runs : groups;
            if (placed[i] is not null)
            {
                dest.Add(placed[i]!);
                continue;
            }

            var best = -1;
            var bestShared = 0;
            for (var p = 0; p < pool.Count; p++)
            {
                if (pool[p].Used || pool[p].IsRun != previous[i].IsRun) continue;
                var shared = SharedCount(previous[i].Set, pool[p].Set);
                if (shared <= bestShared) continue;
                bestShared = shared;
                best = p;
            }

            if (best < 0) continue;
            dest.Add(pool[best].Set);
            pool[best].Used = true;
        }

        foreach (var item in pool)
        {
            if (item.Used) continue;
            (item.IsRun ? runs : groups).Add(item.Set);
        }

        return new Solution { IsValid = true, Runs = runs, Groups = groups };
    }

    private static ValidSet? AdoptGrowth(bool isRun, ValidSet original, ValidSet candidate)
    {
        if (SameCounts(original, candidate)) return original;
        if (!ContainsCounts(candidate, original)) return null;

        var extras = Difference(candidate, original);
        var built = isRun
            ? TryBuildRun(original.Tiles, extras)
            : TryBuildGroup(original.Tiles, extras);
        return built is not null && SameCounts(built, candidate) ? built : null;
    }

    private static SolverResult? Finish(SolverResult solved, Solution previousBoard, List<Tile> played,
        Solution arranged)
    {
        var score = arranged.Runs.Sum(s => s.GetScore()) + arranged.Groups.Sum(s => s.GetScore());
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

    private static int RelevantBits((bool IsRun, ValidSet Set) set, List<Tile> played)
    {
        var bits = 0;
        for (var i = 0; i < played.Count; i++)
        {
            var tile = played[i];
            if (tile.IsJoker)
            {
                bits |= 1 << i;
                continue;
            }

            foreach (var existing in set.Set.Tiles)
            {
                if (existing.IsJoker) continue;
                var sameRun = set.IsRun && existing.Color == tile.Color;
                var sameGroup = !set.IsRun && existing.Value == tile.Value;
                if (sameRun || sameGroup)
                {
                    bits |= 1 << i;
                    break;
                }
            }
        }

        return bits;
    }

    private static int[] Relevance(List<(bool IsRun, ValidSet Set)> sets, List<Tile> played)
    {
        var direct = new int[sets.Count];
        for (var i = 0; i < sets.Count; i++)
        {
            var score = sets[i].Set.Tiles.Any(t => t.IsJoker) ? 8 : 0;
            foreach (var play in played)
            {
                if (play.IsJoker)
                {
                    score += 3;
                    continue;
                }

                foreach (var tile in sets[i].Set.Tiles)
                {
                    if (tile.IsJoker) continue;
                    if (tile.Value == play.Value) score += 6;
                    var distance = Math.Abs(tile.Value - play.Value);
                    if (tile.Color != play.Color || distance == 0) continue;
                    if (distance <= 2) score += 5;
                    else if (distance <= 5) score += 2;
                }
            }

            direct[i] = score;
        }

        var rank = new int[sets.Count];
        for (var i = 0; i < sets.Count; i++)
        {
            var touch = 0;
            for (var j = 0; j < sets.Count; j++)
            {
                if (i == j || direct[j] == 0) continue;
                if (Touches(sets[i].Set, sets[j].Set))
                    touch = Math.Max(touch, direct[j]);
            }

            rank[i] = direct[i] * 10 + touch;
        }

        return rank;
    }

    private static bool Touches(ValidSet a, ValidSet b)
    {
        foreach (var left in a.Tiles)
        {
            if (left.IsJoker) continue;
            foreach (var right in b.Tiles)
            {
                if (right.IsJoker) continue;
                if (left.Value == right.Value) return true;
                if (left.Color == right.Color && Math.Abs(left.Value - right.Value) <= 1) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// One legal run containing every tile of <paramref name="existing"/> plus
    /// <paramref name="added"/>. Existing real tiles keep their relative order; new tiles
    /// are inserted where the sequence needs them, and spare jokers prefer the high end
    /// so an extension reads as an append.
    /// </summary>
    private static ValidSet? TryBuildRun(IReadOnlyList<Tile> existing, IReadOnlyList<Tile> added)
    {
        var reals = new List<Tile>(existing.Count + added.Count);
        var jokers = 0;
        foreach (var tile in existing)
        {
            if (tile.IsJoker) jokers++;
            else reals.Add(tile);
        }

        foreach (var tile in added)
        {
            if (tile.IsJoker) jokers++;
            else reals.Add(tile);
        }

        if (reals.Count == 0 || reals.Count + jokers < 3) return null;

        var color = reals[0].Color;
        var seen = new HashSet<int>();
        foreach (var tile in reals)
        {
            if (tile.Color != color || !seen.Add(tile.Value)) return null;
        }

        var ordered = reals.OrderBy(t => t.Value).ToList();
        var low = ordered[0].Value;
        var high = ordered[^1].Value;
        var span = high - low + 1;
        var gaps = span - ordered.Count;
        if (jokers < gaps) return null;

        var extra = jokers - gaps;
        var roomLeft = low - 1;
        var roomRight = 13 - high;
        if (extra > roomLeft + roomRight) return null;

        var right = Math.Min(extra, roomRight);
        var left = extra - right;
        var byValue = ordered.ToDictionary(t => t.Value);
        var tiles = new Tile[span + extra];
        var cursor = 0;
        for (var value = low - left; value <= high + right; value++)
        {
            tiles[cursor++] = byValue.TryGetValue(value, out var real)
                ? real
                : new Tile(value, color, isJoker: true);
        }

        return new ValidSet(tiles);
    }

    /// <summary>
    /// One legal group. Tiles already on the set stay in their seats; added tiles are appended.
    /// </summary>
    private static ValidSet? TryBuildGroup(IReadOnlyList<Tile> existing, IReadOnlyList<Tile> added)
    {
        var count = existing.Count + added.Count;
        if (count is < 3 or > 4) return null;

        int? value = null;
        var used = new HashSet<TileColor>();
        var jokers = 0;
        foreach (var tile in existing.Concat(added))
        {
            if (tile.IsJoker)
            {
                jokers++;
                continue;
            }

            value ??= tile.Value;
            if (tile.Value != value.Value || !used.Add(tile.Color)) return null;
        }

        if (value is null) return null;

        var free = new HashSet<TileColor>(Enum.GetValues<TileColor>());
        free.ExceptWith(used);
        if (jokers > free.Count) return null;

        Tile PlaceJoker(Tile joker)
        {
            if (!free.Remove(joker.Color))
            {
                var color = default(TileColor);
                foreach (var candidate in Enum.GetValues<TileColor>())
                {
                    if (!free.Remove(candidate)) continue;
                    color = candidate;
                    break;
                }

                return new Tile(value.Value, color, isJoker: true);
            }

            return new Tile(value.Value, joker.Color, isJoker: true);
        }

        var tiles = new List<Tile>(count);
        foreach (var tile in existing)
            tiles.Add(tile.IsJoker ? PlaceJoker(tile) : tile);
        foreach (var tile in added)
            tiles.Add(tile.IsJoker ? PlaceJoker(tile) : tile);

        return new ValidSet(tiles.ToArray());
    }

    private static List<Tile> PlayedTiles(SolverResult solved)
    {
        var played = solved.TilesToPlay.Where(t => !t.IsJoker).ToList();
        var jokers = solved.JokersToPlay + solved.TilesToPlay.Count(t => t.IsJoker);
        for (var i = 0; i < jokers; i++)
            played.Add(new Tile(isJoker: true));
        return played;
    }

    private static List<Tile> TilesFor(List<Tile> played, int mask)
    {
        var tiles = new List<Tile>();
        for (var i = 0; i < played.Count; i++)
            if ((mask & (1 << i)) != 0)
                tiles.Add(played[i]);
        return tiles;
    }

    private static List<(bool IsRun, ValidSet Set)> ListSets(Solution board) =>
    [
        ..board.Runs.Select(s => (true, s)),
        ..board.Groups.Select(s => (false, s))
    ];

    private static IEnumerable<int[]> Combinations(int[] candidates, int size)
    {
        var combo = new int[size];

        IEnumerable<int[]> Walk(int start, int depth)
        {
            if (depth == size)
            {
                yield return (int[])combo.Clone();
                yield break;
            }

            for (var i = start; i <= candidates.Length - (size - depth); i++)
            {
                combo[depth] = candidates[i];
                foreach (var found in Walk(i + 1, depth + 1))
                    yield return found;
            }
        }

        return Walk(0, 0);
    }

    private readonly record struct CountKey(bool IsJoker, int Value, TileColor Color);

    private static Dictionary<CountKey, int> Counts(ValidSet set)
    {
        var counts = new Dictionary<CountKey, int>();
        foreach (var tile in set.Tiles)
        {
            var key = tile.IsJoker
                ? new CountKey(true, 0, default)
                : new CountKey(false, tile.Value, tile.Color);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts;
    }

    private static bool SameCounts(ValidSet a, ValidSet b)
    {
        var left = Counts(a);
        var right = Counts(b);
        if (left.Count != right.Count) return false;
        foreach (var (key, count) in left)
            if (right.GetValueOrDefault(key) != count)
                return false;
        return true;
    }

    private static bool ContainsCounts(ValidSet container, ValidSet inner)
    {
        var have = Counts(container);
        foreach (var (key, count) in Counts(inner))
            if (have.GetValueOrDefault(key) < count)
                return false;
        return true;
    }

    private static List<Tile> Difference(ValidSet container, ValidSet inner)
    {
        var have = Counts(inner);
        var extras = new List<Tile>();
        foreach (var tile in container.Tiles)
        {
            var key = tile.IsJoker
                ? new CountKey(true, 0, default)
                : new CountKey(false, tile.Value, tile.Color);
            if (have.GetValueOrDefault(key) > 0)
            {
                have[key]--;
                continue;
            }

            extras.Add(tile.IsJoker ? new Tile(isJoker: true) : tile);
        }

        return extras;
    }

    private static int SharedCount(ValidSet a, ValidSet b)
    {
        var counts = Counts(a);
        var shared = 0;
        foreach (var tile in b.Tiles)
        {
            var key = tile.IsJoker
                ? new CountKey(true, 0, default)
                : new CountKey(false, tile.Value, tile.Color);
            if (counts.GetValueOrDefault(key) <= 0) continue;
            counts[key]--;
            shared++;
        }

        return shared;
    }

    private sealed class PoolSet(bool isRun, ValidSet set)
    {
        public bool IsRun { get; } = isRun;
        public ValidSet Set { get; set; } = set;
        public bool Used { get; set; }
    }
}
