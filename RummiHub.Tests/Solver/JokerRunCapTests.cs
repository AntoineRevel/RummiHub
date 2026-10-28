using AwesomeAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;

namespace RummiHub.Tests.Solver;

/// <summary>
/// Regression: legacy GetRuns appended a joker as value+1 with no cap, so a run
/// ending at 13 became 14 (and a second joker 15). GetScore then counted that face.
/// A joker may still extend the low end when that value stays in 1..13.
/// </summary>
public class JokerRunCapTests
{
    private static Set Of(params Tile[] tiles) => new(tiles.ToList());

    private static void ShouldBeLegalRun(SolverResult result, Set board, Set rack, bool hasPlayed, int score)
    {
        result.Found.Should().BeTrue();
        result.Score.Should().Be(score);
        result.BestSolution.Sets
            .SelectMany(s => s.Tiles)
            .Should().OnlyContain(t => t.Value >= 1 && t.Value <= 13);
        SolutionValidator.IsValid(result, board, rack, hasPlayed).Should().BeTrue();
    }

    [Fact]
    public void FirstPlay_12_13_AndJoker_Scores36_Not39()
    {
        var rack = Of(new Tile(12, TileColor.Blue), new Tile(13, TileColor.Blue), new Tile(isJoker: true));

        var graph = GraphFirstSolver.Create(rack).SearchSolution();
        var dynamic = DynamicSolver.CreateFirst(rack).SearchSolution();

        ShouldBeLegalRun(graph, new Set(), rack, hasPlayed: false, score: 36);
        ShouldBeLegalRun(dynamic, new Set(), rack, hasPlayed: false, score: 36);
        graph.BestSolution.Sets.SelectMany(r => r.Tiles).Should().Contain(t => t.IsJoker && t.Value == 11);
    }

    [Fact]
    public void FirstPlay_13_AndTwoJokers_Scores36_Not42()
    {
        var rack = Of(new Tile(13, TileColor.Black), new Tile(isJoker: true), new Tile(isJoker: true));

        var graph = GraphFirstSolver.Create(rack).SearchSolution();

        ShouldBeLegalRun(graph, new Set(), rack, hasPlayed: false, score: 36);
        graph.BestSolution.Sets.SelectMany(r => r.Tiles)
            .Where(t => t.IsJoker).Select(t => t.Value).Should().BeEquivalentTo([11, 12]);
    }

    [Fact]
    public void FirstPlay_11_12_13_AndJoker_DoesNotScoreTheJokerAs14()
    {
        var rack = Of(
            new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue),
            new Tile(13, TileColor.Blue), new Tile(isJoker: true));

        var graph = GraphFirstSolver.Create(rack).SearchSolution();
        var dynamic = DynamicSolver.CreateFirst(rack).SearchSolution();

        // [10j, 11, 12, 13] = 46. Counting the joker as 14 produced 50.
        ShouldBeLegalRun(graph, new Set(), rack, hasPlayed: false, score: 46);
        ShouldBeLegalRun(dynamic, new Set(), rack, hasPlayed: false, score: 46);
    }

    [Fact]
    public void DuringGame_Board11_12_13_JokerExtendsDownTo10()
    {
        var board = Of(new Tile(11, TileColor.Red), new Tile(12, TileColor.Red), new Tile(13, TileColor.Red));
        var rack = Of(new Tile(isJoker: true));

        var graph = GraphSolver.Create(board, rack).SearchSolution();
        var dynamic = DynamicSolver.Create(board, rack).SearchSolution();

        ShouldBeLegalRun(graph, board, rack, hasPlayed: true, score: 46);
        ShouldBeLegalRun(dynamic, board, rack, hasPlayed: true, score: 46);
        graph.JokersToPlay.Should().Be(1);
        graph.BestSolution.Sets.SelectMany(r => r.Tiles).Should().Contain(t => t.IsJoker && t.Value == 10);
    }

    [Fact]
    public void DuringGame_Board12_13_JokerIsRequiredToOpenTheRun()
    {
        var board = Of(new Tile(12, TileColor.Mango), new Tile(13, TileColor.Mango));
        var rack = Of(new Tile(isJoker: true));

        var graph = GraphSolver.Create(board, rack).SearchSolution();

        ShouldBeLegalRun(graph, board, rack, hasPlayed: true, score: 36);
        graph.BestSolution.ToSet().Tiles.Should().Contain(board.Tiles);
    }

    [Fact]
    public void DuringGame_UpwardJokerOn12_StaysAt13()
    {
        // Room above 12: the joker is 13, not a downward 10 and not a 14.
        var board = Of(new Tile(10, TileColor.Blue), new Tile(11, TileColor.Blue), new Tile(12, TileColor.Blue));
        var rack = Of(new Tile(isJoker: true));

        var graph = GraphSolver.Create(board, rack).SearchSolution();

        ShouldBeLegalRun(graph, board, rack, hasPlayed: true, score: 46);
        graph.BestSolution.Sets.SelectMany(r => r.Tiles).Should().Contain(t => t.IsJoker && t.Value == 13);
    }
}
