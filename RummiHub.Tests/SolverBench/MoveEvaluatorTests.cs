using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.SolverBench.Measure;

namespace RummiHub.Tests.SolverBench;

public class MoveEvaluatorTests
{
    private static SolverResult Result(Solution solution, int reportedScore, int jokers = 0)
    {
        solution.IsValid = true;
        return SolverResult.FromSolution("test", solution, tilesToPlay: [], jokersToPlay: jokers, score: reportedScore);
    }

    [Fact]
    public void Normalize_ScoresGroupJoker_AtGroupValue()
    {
        var solution = new Solution();
        solution.Sets.Add(new ValidSet([new Tile(5, TileColor.Red), new Tile(5, TileColor.Black), new Tile(isJoker: true)]));

        // Solver reported 0 (typical for non-scoring solvers); evaluator recovers 5+5+5 = 15.
        MoveEvaluator.Normalize(Result(solution, reportedScore: 0, jokers: 1)).Score.Should().Be(15);
    }

    [Fact]
    public void Normalize_ScoresRunJoker_AtFilledValue()
    {
        var solution = new Solution();
        solution.Sets.Add(new ValidSet([new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(isJoker: true)]));

        // The joker fills 12 → 10 + 11 + 12 = 33.
        MoveEvaluator.Normalize(Result(solution, reportedScore: 0, jokers: 1)).Score.Should().Be(33);
    }

    [Fact]
    public void OverScores_DetectsJokerCountedAboveThirteen()
    {
        // Mimics the GraphSolver bug: a [12,13,joker] run scored as 12+13+14 = 39, but the only
        // legal realization is [11,12,13] = 36.
        var solution = new Solution();
        solution.Sets.Add(new ValidSet([new Tile(12, TileColor.Blue), new Tile(13, TileColor.Blue), new Tile(isJoker: true)]));

        var result = Result(solution, reportedScore: 39, jokers: 1);

        MoveEvaluator.OverScores(result).Should().BeTrue();
        MoveEvaluator.Normalize(result).Score.Should().Be(36);
    }

    [Fact]
    public void OverScores_IsFalse_WhenReportedScoreMatchesLegalScore()
    {
        var solution = new Solution();
        solution.Sets.Add(new ValidSet([new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue)]));

        MoveEvaluator.OverScores(Result(solution, reportedScore: 33)).Should().BeFalse();
    }

    [Fact]
    public void Normalize_NotFoundResult_IsReturnedUnchanged()
    {
        var invalid = SolverResult.Invalid("test");
        MoveEvaluator.Normalize(invalid).Found.Should().BeFalse();
    }
}
