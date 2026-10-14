using System.Diagnostics;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.Solver.Worker.Solver.Dynamic;

/// <summary>
/// Post-processes a during-game solve so the optimal move disturbs the table as little as
/// possible. Solvers rebuild the whole board from scratch, so even "add one tile to a group"
/// can come back as a fully reshuffled table; players read that as chaos. Sets that were not
/// involved in the move should come back IDENTICAL.
///
/// <para>
/// Given the board as currently arranged (the request's structured Runs/Groups) and the
/// solver's optimal move, this searches for the smallest collection of existing sets that,
/// broken up and pooled with the played tiles, recomposes exactly into valid sets — every
/// other set is returned untouched. The move itself is never altered: same tiles played,
/// same optimality, only the arrangement changes. Combinations are tried by number of broken
/// sets first (0, 1, 2, …), then by fewest tiles broken, so the first hit moves the least.
/// When no local rearrangement exists (the move genuinely requires restructuring the table)
/// or the search budget runs out, the solver's own arrangement is returned as-is.
/// </para>
/// </summary>
public static class BoardStabilizer
{
    /// <summary>Most existing sets a move is allowed to break before we stop searching.</summary>
    private const int MaxBrokenSets = 4;

    // Each attempt is a micro-solve (a few sets + the played tiles, µs–ms), but combination
    // counts explode on huge boards. Past these budgets the solver's arrangement wins.
    private const int MaxAttempts = 600;
    private const long MaxCombinationsPerSize = 50_000;
    private static readonly TimeSpan MaxSearchTime = TimeSpan.FromMilliseconds(300);

    public static SolverResult Stabilize(Solution previousBoard, SolverResult solved,
        CancellationToken cancellationToken = default)
    {
        if (!solved.Found) return solved;

        // TilesToPlay carries real tiles only in practice, but count defensively.
        var playedTiles = solved.TilesToPlay.Where(t => !t.IsJoker).ToList();
        var playedJokers = solved.JokersToPlay + solved.TilesToPlay.Count(t => t.IsJoker);
        if (playedTiles.Count + playedJokers == 0) return solved;

        var previousSets = ListSets(previousBoard);
        if (previousSets.Count == 0) return solved;

        var stopwatch = Stopwatch.StartNew();
        var attemptsLeft = MaxAttempts;

        // Breaking ALL sets is the original problem the solver already answered — skip it.
        var maxBroken = Math.Min(MaxBrokenSets, previousSets.Count - 1);
        for (var brokenCount = 0; brokenCount <= maxBroken; brokenCount++)
        {
            foreach (var combo in CombinationsByFewestTiles(previousSets, brokenCount))
            {
                if (--attemptsLeft < 0 || stopwatch.Elapsed > MaxSearchTime
                                       || cancellationToken.IsCancellationRequested)
                    return solved;

                var recomposed = TryRecompose(previousSets, combo, playedTiles, playedJokers,
                    cancellationToken);
                if (recomposed is null) continue;

                var stabilized = AssembleSolution(previousSets, combo, recomposed);
                var score = stabilized.Runs.Sum(s => s.GetScore())
                            + stabilized.Groups.Sum(s => s.GetScore());
                var result = SolverResult.FromSolution(solved.Source, stabilized,
                    solved.TilesToPlay, solved.JokersToPlay, score);

                // Defensive: the stabilized arrangement must still be a fully legal move over
                // the same board and played tiles; on any assembly bug, keep the solver's.
                return IsLegalRearrangement(result, previousBoard, playedTiles, playedJokers)
                    ? result
                    : solved;
            }
        }

        return solved;
    }

    /// <summary>
    /// Tests whether the tiles of the <paramref name="combo"/> sets plus the played tiles
    /// recompose EXACTLY into valid sets. The broken sets are passed as the forced "board"
    /// side of the solver (each is legal on its own, so keeping them all is always feasible
    /// and the optimum never drops one) and the played tiles as the rack side; the cover is
    /// exact if and only if every played tile gets used.
    /// </summary>
    private static Solution? TryRecompose(List<(bool IsRun, ValidSet Set)> previousSets,
        int[] combo, List<Tile> playedTiles, int playedJokers, CancellationToken cancellationToken)
    {
        var broken = new Set();
        foreach (var index in combo)
            broken.Concat(previousSets[index].Set);

        var played = new Set(playedTiles);
        for (var i = 0; i < playedJokers; i++)
            played.AddTile(new Tile(isJoker: true));

        var sub = DynamicSolver.Create(broken, played).SearchSolution(cancellationToken);
        if (!sub.Found) return null;
        if (sub.TilesToPlay.Count() != playedTiles.Count || sub.JokersToPlay != playedJokers)
            return null;

        return sub.BestSolution;
    }

    private static Solution AssembleSolution(List<(bool IsRun, ValidSet Set)> previousSets,
        int[] combo, Solution recomposed)
    {
        var broken = new HashSet<int>(combo);
        var solution = new Solution { IsValid = true };

        for (var i = 0; i < previousSets.Count; i++)
        {
            if (broken.Contains(i)) continue;
            if (previousSets[i].IsRun) solution.AddRun(previousSets[i].Set);
            else solution.AddGroup(previousSets[i].Set);
        }

        solution.Runs.AddRange(recomposed.Runs);
        solution.Groups.AddRange(recomposed.Groups);
        return solution;
    }

    private static bool IsLegalRearrangement(SolverResult result, Solution previousBoard,
        List<Tile> playedTiles, int playedJokers)
    {
        var rack = new Set(playedTiles);
        for (var i = 0; i < playedJokers; i++)
            rack.AddTile(new Tile(isJoker: true));

        return SolutionValidator.IsValid(result, previousBoard.ToSet(), rack, hasPlayed: true);
    }

    private static List<(bool IsRun, ValidSet Set)> ListSets(Solution board) =>
    [
        ..board.Runs.Select(s => (true, s)),
        ..board.Groups.Select(s => (false, s))
    ];

    /// <summary>
    /// All index combinations of the given size, smallest total broken-tile count first, so
    /// within one break-count level the least disruptive candidates are attempted first.
    /// Generation is skipped entirely when the count would dwarf the attempt budget anyway.
    /// </summary>
    private static IEnumerable<int[]> CombinationsByFewestTiles(
        List<(bool IsRun, ValidSet Set)> sets, int size)
    {
        if (size == 0) return [[]];
        if (CombinationCount(sets.Count, size) > MaxCombinationsPerSize) return [];

        var combos = new List<int[]>();
        var indices = new int[size];
        Collect(0, 0);

        return combos.OrderBy(combo => combo.Sum(i => sets[i].Set.Tiles.Length));

        void Collect(int start, int depth)
        {
            if (depth == size)
            {
                combos.Add((int[])indices.Clone());
                return;
            }

            for (var i = start; i <= sets.Count - (size - depth); i++)
            {
                indices[depth] = i;
                Collect(i + 1, depth + 1);
            }
        }
    }

    private static long CombinationCount(int n, int k)
    {
        var count = 1L;
        for (var i = 0; i < k; i++)
        {
            count = count * (n - i) / (i + 1);
            if (count > MaxCombinationsPerSize) return long.MaxValue;
        }

        return count;
    }
}
