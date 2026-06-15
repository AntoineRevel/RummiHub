using System.Collections.Concurrent;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.Solver.Worker.Solver.Graph.First;

public class GraphFirstSolver : ISolver
{
    private readonly int _jokers;
    private readonly Tile[] _tiles;

    private GraphFirstSolver(Tile[] tiles, int jokers)
    {
        _tiles = tiles;
        _jokers = jokers;
    }

    public SolverResult SearchSolution(CancellationToken cancellationToken = default)
    {
        var root = RummiFirstNode.CreateRoot(_tiles, _jokers);
        var currentLevel = new ConcurrentBag<RummiFirstNode> { root };

        while (!cancellationToken.IsCancellationRequested)
        {
            var nextLevel = new ConcurrentBag<RummiFirstNode>();

            Parallel.ForEach(currentLevel,
                new ParallelOptions { CancellationToken = cancellationToken },
                node =>
                {
                    node.GetChildren();
                    foreach (var child in node.Children) nextLevel.Add(child);
                }
            );

            if (nextLevel.IsEmpty) break;
            currentLevel = nextLevel;
        }

        if (root.LeafNodes.IsEmpty) return SolverResult.Invalid(nameof(GraphFirstSolver));

        var bestNode = root.LeafNodes.MaxBy(node => node.Score);

        // No play reaches 30: no legal move, but carry the best score so the UI can display it.
        if (bestNode!.Score < ISolver.MinScore) return SolverResult.Invalid(nameof(GraphFirstSolver), bestNode!.Score);

        var bestSolution = bestNode.GetSolution();

        var tilesToPlay = _tiles.Where((_, i) => bestNode.IsTileUsed[i]).ToArray();
        var jokerToPlay = _jokers - bestNode.Jokers;

        return SolverResult.FromSolution(nameof(GraphFirstSolver), bestSolution, tilesToPlay, jokerToPlay,
            bestNode.Score);
    }

    public static GraphFirstSolver Create(Set playerSet)
    {
        playerSet.Tiles.Sort();
        return new GraphFirstSolver(
            playerSet.Tiles.ToArray(),
            playerSet.Jokers
        );
    }
}
