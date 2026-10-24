using System.Collections.Concurrent;
using RummiHub.Core.Models;
using RummiHub.Solver.Worker.Solver.Abstract;

namespace RummiHub.Solver.Worker.Solver.Graph.First;

public class RummiFirstNode : BaseSolver
{
    private readonly RummiFirstNode? _parentNode;
    private readonly ValidSet _set;
    private readonly int _startIndex;
    public readonly List<RummiFirstNode> Children = [];

    public readonly bool[] IsTileUsed;
    public readonly ConcurrentBag<RummiFirstNode> LeafNodes;
    public readonly int Score;

    private RummiFirstNode(ValidSet set, Tile[] tiles, bool[] isTileUsed, int jokers, int startIndex,
        ConcurrentBag<RummiFirstNode> leafNodes, int score, RummiFirstNode? parentNode) :
        base(tiles, jokers)
    {
        IsTileUsed = (bool[])isTileUsed.Clone();
        Array.Copy(IsTileUsed, UsedTiles, Tiles.Length);
        _set = set;
        _startIndex = startIndex;
        LeafNodes = leafNodes;
        Score = score;
        _parentNode = parentNode;
    }

    public static RummiFirstNode CreateRoot(Tile[] tiles, int jokers)
    {
        return new RummiFirstNode(new ValidSet([]), tiles, new bool[tiles.Length], jokers, 0, [], 0, null);
    }

    public void GetChildren()
    {
        var createdNode = 0;

        for (var i = _startIndex; i < Tiles.Length; i++)
        {
            if (IsTileUsed[i]) continue;

            UsedTiles[i] = true;
            foreach (var set in GetRuns(i)) CreateChildNode(ref createdNode, i, set);
            foreach (var set in GetGroups(i)) CreateChildNode(ref createdNode, i, set);
            UsedTiles[i] = false;
        }

        if (createdNode == 0) LeafNodes.Add(this);
    }

    private void CreateChildNode(ref int createdNode, int index, ValidSet set)
    {
        createdNode++;
        MarkTilesAsUsed(set, index);
        var child = new RummiFirstNode(set, Tiles, UsedTiles, Jokers, index + 1, LeafNodes,
            Score + set.GetScore(), this);
        MarkTilesAsUnused(set, index);
        Children.Add(child);
    }

    public Solution GetSolution()
    {
        var solution = new Solution { IsValid = true };
        var currentNode = this;

        while (currentNode._parentNode is not null)
        {
            solution.Sets.Add(currentNode._set);
            currentNode = currentNode._parentNode;
        }

        return solution;
    }
}
