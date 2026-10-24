using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.SolverBench.Measure;

/// <summary>
/// Normalizes a solver result so the independent <c>SolutionValidator</c> judges the move on
/// its merits rather than on incidental representation choices. Different solvers encode the
/// same legal move differently: some report <c>Score = 0</c>, some leave joker tiles without
/// their represented value. This evaluator reconstructs a legal realization of each set
/// (assigning every joker a concrete value) and computes the true score, while leaving a
/// genuinely impossible set untouched so it is still rejected.
///
/// <para>It also exposes <see cref="OverScores"/>, which detects a solver that reports a higher
/// score than the move can legally yield — the classic <c>GraphSolver</c> "joker as a value
/// above 13" over-scoring bug, whose tiles are otherwise legal.</para>
/// </summary>
public static class MoveEvaluator
{
    /// <summary>Builds a result whose joker values and score are made explicit and legal where possible.</summary>
    public static SolverResult Normalize(SolverResult result)
    {
        if (!result.Found || !result.BestSolution.IsValid) return result;

        var normalized = new Solution { IsValid = true };
        var score = 0;

        foreach (var set in result.BestSolution.Sets)
        {
            var realized = set.IsGroup ? RealizeGroup(set.Tiles) : RealizeRun(set.Tiles);
            normalized.Sets.Add(new ValidSet(realized ?? set.Tiles));
            score += (realized ?? set.Tiles).Sum(t => t.Value);
        }

        return SolverResult.FromSolution(result.Source, normalized, result.TilesToPlay, result.JokersToPlay, score);
    }

    /// <summary>True when the solver claims a strictly higher score than the legal realization allows.</summary>
    public static bool OverScores(SolverResult result)
    {
        if (!result.Found) return false;
        var legal = Normalize(result);
        return result.Score > legal.Score;
    }

    /// <summary>
    /// Assigns concrete values to a run's jokers to form a legal consecutive same-color run.
    /// Returns null when no legal placement exists (a genuinely illegal run).
    /// </summary>
    private static Tile[]? RealizeRun(Tile[] tiles)
    {
        var n = tiles.Length;
        var reals = tiles.Where(t => !t.IsJoker).ToList();
        var jokers = n - reals.Count;
        if (reals.Count == 0) return null; // an all-joker "run" carries no color/anchor

        var color = reals[0].Color;
        if (reals.Any(t => t.Color != color)) return null;

        var values = reals.Select(t => t.Value).OrderBy(v => v).ToList();
        if (values.Distinct().Count() != values.Count) return null; // a run has no repeats

        int min = values[0], max = values[^1];
        if (max - min + 1 > n) return null; // reals too spread to fit in n slots

        // Pick a window [lo, lo+n-1] inside [1,13] that covers every real value.
        var lo = min;
        if (lo + n - 1 > 13) lo = 13 - n + 1; // shift down to stay within 13
        if (lo < 1 || lo > min || lo + n - 1 < max) return null; // cannot fit legally

        var present = values.ToHashSet();
        var realized = new Tile[n];
        var jokersLeft = jokers;
        for (var i = 0; i < n; i++)
        {
            var v = lo + i;
            if (present.Contains(v)) realized[i] = new Tile(v, color);
            else { realized[i] = new Tile(v, color, isJoker: true); jokersLeft--; }
        }

        return jokersLeft == 0 ? realized : null;
    }

    /// <summary>
    /// Assigns concrete colors to a group's jokers to form a legal same-value group of distinct
    /// colors. Returns null when no legal placement exists.
    /// </summary>
    private static Tile[]? RealizeGroup(Tile[] tiles)
    {
        if (tiles.Length is < 3 or > 4) return null;

        var reals = tiles.Where(t => !t.IsJoker).ToList();
        var jokers = tiles.Length - reals.Count;
        if (reals.Count == 0) return null;

        var value = reals[0].Value;
        if (reals.Any(t => t.Value != value)) return null;

        var usedColors = reals.Select(t => t.Color).ToHashSet();
        if (usedColors.Count != reals.Count) return null; // duplicate colors

        var freeColors = Enum.GetValues<TileColor>().Where(c => !usedColors.Contains(c)).ToList();
        if (jokers > freeColors.Count) return null;

        var realized = new List<Tile>(reals);
        for (var i = 0; i < jokers; i++)
            realized.Add(new Tile(value, freeColors[i], isJoker: true));

        return realized.ToArray();
    }
}
