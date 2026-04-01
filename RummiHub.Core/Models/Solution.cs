namespace RummiHub.Core.Models;

/// <summary>
/// Represents a complete board state as a collection of valid Runs and Groups.
/// Used both as solver output and as the canonical board representation.
/// </summary>
public class Solution
{
    public static readonly Solution InvalidSolution = new() { IsValid = false };

    public List<ValidSet> Runs { get; set; } = [];
    public List<ValidSet> Groups { get; set; } = [];
    public bool IsValid { get; set; }

    public Solution AddSolution(Solution solution)
    {
        Groups.AddRange(solution.Groups);
        Runs.AddRange(solution.Runs);
        return this;
    }

    public void AddRun(ValidSet run) => Runs.Add(run);

    public void AddGroup(ValidSet group) => Groups.Add(group);

    /// <summary>
    /// Flattens this Solution into a <see cref="Set"/> (tiles + joker count).
    /// Used by solvers which take the board as a flat tile collection.
    /// </summary>
    public Set ToSet()
    {
        var result = new Set();

        foreach (var run in Runs) result.Concat(run);
        foreach (var group in Groups) result.Concat(group);

        return result;
    }
}
