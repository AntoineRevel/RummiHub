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

    public static SolverResult Invalid(string source) =>
        new()
        {
            Source = source,
            Found = false,
            BestSolution = new Solution()
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
