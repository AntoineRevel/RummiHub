using RummiHub.Core.Models;

namespace RummiHub.Core.Solver;

public class SolverResult
{
    private SolverResult()
    {
        Source = string.Empty;
        Found = false;
        BestSolution = new Solution();
        TilesToPlay = [];
        JokersToPlay = 0;
        Score = 0;
    }

    public string Source { get; private init; }
    public bool Found { get; private init; }
    public Solution BestSolution { get; private init; }
    public IEnumerable<Tile> TilesToPlay { get; private init; }
    public int JokersToPlay { get; private init; }
    public int Score { get; private init; }

    /// <summary>
    /// A result with no playable move. <paramref name="score"/> lets a solver still report the
    /// best score it could reach (e.g. a first play that forms a legal set but falls short of the
    /// 30-point threshold), so the UI can tell the player how close they were instead of showing 0.
    /// </summary>
    public static SolverResult Invalid(string source, int score = 0) =>
        new()
        {
            Source = source,
            Found = false,
            BestSolution = new Solution(),
            Score = score
        };

    public static SolverResult FromSolution(string source, Solution solution, IEnumerable<Tile> tilesToPlay,
        int jokersToPlay = 0, int score = 0)
    {
        if (!solution.IsValid) return Invalid(source);

        return new SolverResult
        {
            Source = source,
            Found = true,
            BestSolution = solution,
            TilesToPlay = tilesToPlay,
            JokersToPlay = jokersToPlay,
            Score = score
        };
    }
}
