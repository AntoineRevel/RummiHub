namespace RummiHub.Core.Models;

/// <summary>
/// Represents a complete board state as one ordered list of valid sets, runs and groups
/// mixed. The order is the reading order on screen: a set the move rebuilt sits where its
/// tiles came from, whatever its kind.
/// Used both as solver output and as the canonical board representation.
/// </summary>
public class Solution
{
    public static readonly Solution InvalidSolution = new() { IsValid = false };

    public List<ValidSet> Sets { get; set; } = [];
    public bool IsValid { get; set; }

    public Solution AddSolution(Solution solution)
    {
        Sets.AddRange(solution.Sets);
        return this;
    }

    /// <summary>
    /// Flattens this Solution into a <see cref="Set"/> (tiles + joker count).
    /// Used by solvers which take the board as a flat tile collection.
    /// </summary>
    public Set ToSet()
    {
        var result = new Set();

        foreach (var set in Sets) result.Concat(set);

        return result;
    }
}
