using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.Solver.Worker.Solver.Dynamic;

/// <summary>
/// Independent, defensive checker used to decide whether a <see cref="SolverResult"/> is a
/// fully legal Rummikub move. It re-derives legality from scratch (it trusts nothing the
/// solver claims) so a bug in a new engine can be caught and a fallback triggered.
/// </summary>
public static class SolutionValidator
{
    public static bool IsValid(SolverResult result, Set board, Set rack, bool hasPlayed)
    {
        if (!result.Found || !result.BestSolution.IsValid) return false;
        if (result.JokersToPlay < 0 || result.JokersToPlay > rack.Jokers) return false;

        var solution = result.BestSolution;

        if (!solution.Runs.All(IsLegalRun)) return false;
        if (!solution.Groups.All(IsLegalGroup)) return false;

        // Every played tile must come from the rack (multiset containment).
        if (!IsSubMultiset(result.TilesToPlay.Where(t => !t.IsJoker), rack.Tiles)) return false;

        // Tile conservation: solution == board + played, both for colored tiles and jokers.
        var solutionTiles = Histogram(solution.Runs.Concat(solution.Groups)
            .SelectMany(s => s.Tiles).Where(t => !t.IsJoker));
        var expectedTiles = Histogram(board.Tiles.Concat(result.TilesToPlay.Where(t => !t.IsJoker)));
        if (!SameHistogram(solutionTiles, expectedTiles)) return false;

        var solutionJokers = solution.Runs.Concat(solution.Groups).SelectMany(s => s.Tiles).Count(t => t.IsJoker);
        if (solutionJokers != board.Jokers + result.JokersToPlay) return false;

        if (hasPlayed)
        {
            // During the game a real move must add at least one tile or joker to the board.
            if (!result.TilesToPlay.Any(t => !t.IsJoker) && result.JokersToPlay == 0) return false;
        }
        else
        {
            // First play: no board involved, and the score gate must be met.
            if (board.Tiles.Count > 0 || board.Jokers > 0) return false;
            if (result.Score < ISolver.MinScore) return false;
        }

        return true;
    }

    private static bool IsLegalRun(ValidSet set)
    {
        var tiles = set.Tiles;
        if (tiles.Length < 3) return false;

        TileColor? color = null;
        var ordered = tiles.OrderBy(t => t.Value).ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            var tile = ordered[i];
            if (tile.Value is < 1 or > 13) return false;
            if (!tile.IsJoker)
            {
                if (color is null) color = tile.Color;
                else if (color != tile.Color) return false;
            }

            if (i > 0 && ordered[i].Value != ordered[i - 1].Value + 1) return false;
        }

        return true;
    }

    private static bool IsLegalGroup(ValidSet set)
    {
        var tiles = set.Tiles;
        if (tiles.Length is < 3 or > 4) return false;

        int? value = null;
        var seenColors = new HashSet<TileColor>();

        foreach (var tile in tiles)
        {
            if (tile.Value is < 1 or > 13) return false;
            if (tile.IsJoker) continue;

            if (value is null) value = tile.Value;
            else if (value != tile.Value) return false;

            if (!seenColors.Add(tile.Color)) return false;
        }

        return true;
    }

    private static Dictionary<byte, int> Histogram(IEnumerable<Tile> tiles)
    {
        var histogram = new Dictionary<byte, int>();
        foreach (var tile in tiles)
        {
            var key = (byte)((int)tile.Color << 4 | tile.Value);
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }

        return histogram;
    }

    private static bool SameHistogram(Dictionary<byte, int> a, Dictionary<byte, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (key, count) in a)
            if (b.GetValueOrDefault(key) != count)
                return false;
        return true;
    }

    private static bool IsSubMultiset(IEnumerable<Tile> subset, IEnumerable<Tile> superset)
    {
        var available = Histogram(superset);
        foreach (var tile in subset)
        {
            var key = (byte)((int)tile.Color << 4 | tile.Value);
            var remaining = available.GetValueOrDefault(key);
            if (remaining <= 0) return false;
            available[key] = remaining - 1;
        }

        return true;
    }
}
