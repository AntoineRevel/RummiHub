using System.Collections.Concurrent;
using RummiHub.Core.Models;
using RummiHub.Solver.Worker.Solver.Abstract;

namespace RummiHub.Solver.Worker.Solver.Graph;

public class RummiNode : BaseSolver
{
    private readonly int _boardJokers;
    private readonly int _boardTileNotPlayed;
    private readonly bool[] _isPlayerTile;
    private readonly bool _isRun;
    private readonly RummiNode? _parentNode;
    private readonly ValidSet _set;
    private readonly int _startIndex;
    private readonly int _totalJoker;

    public readonly List<RummiNode> Children = [];
    public readonly bool[] IsTileUsed;
    public readonly ConcurrentBag<RummiNode> LeafNodes;
    public readonly int PlayerTilePlayed;
    public readonly int Score;

    private RummiNode(ValidSet set, Tile[] tiles, bool[] isTileUsed, int jokers, int startIndex,
        ConcurrentBag<RummiNode> leafNodes, int score, RummiNode? parentNode, bool isRun, bool[] isPlayerTile,
        int boardJokers, int totalJoker, int playerTilePlayed, int boardTileNotPlayed) :
        base(tiles, jokers)
    {
        IsTileUsed = (bool[])isTileUsed.Clone();
        Array.Copy(IsTileUsed, UsedTiles, Tiles.Length);
        _set = set;
        _startIndex = startIndex;
        LeafNodes = leafNodes;
        Score = score;
        _parentNode = parentNode;
        _isRun = isRun;
        _isPlayerTile = isPlayerTile;
        _boardJokers = boardJokers;
        _totalJoker = totalJoker;
        PlayerTilePlayed = playerTilePlayed;
        _boardTileNotPlayed = boardTileNotPlayed;
    }

    public static RummiNode CreateRoot(Tile[] tiles, int jokers, bool[] isPlayerTile, int boardTile, int boardJokers)
    {
        return new RummiNode(new ValidSet([]), tiles, new bool[tiles.Length], jokers, 0, [], 0, null, false,
            isPlayerTile, boardJokers, jokers, 0, boardTile);
    }

    public void GetChildren()
    {
        var totalCreatedNode = 0;

        for (var i = _startIndex; i < Tiles.Length; i++)
        {
            if (IsTileUsed[i]) continue;
            var createdNode = 0;
            UsedTiles[i] = true;
            var playerTilePlayed = PlayerTilePlayed;
            var boardTileNotPlayed = _boardTileNotPlayed;
            if (_isPlayerTile[i]) playerTilePlayed++;
            else boardTileNotPlayed--;
            foreach (var set in GetRuns(i))
                CreateChildNode(ref createdNode, i, set, true, playerTilePlayed, boardTileNotPlayed);
            foreach (var set in GetGroups(i))
                CreateChildNode(ref createdNode, i, set, false, playerTilePlayed, boardTileNotPlayed);

            if (createdNode == 0 && !_isPlayerTile[i]) return;

            UsedTiles[i] = false;

            totalCreatedNode += createdNode;
        }

        var jokerPlayer = _totalJoker - Jokers - _boardJokers;
        if (totalCreatedNode == 0 && _boardTileNotPlayed == 0 && (PlayerTilePlayed > 0 || jokerPlayer > 0))
            LeafNodes.Add(this);
    }

    private void CreateChildNode(ref int createdNode, int index, ValidSet set, bool isRun, int playerTilePlayed,
        int boardTileNotPlayed)
    {
        createdNode++;
        MarkTilesAsUsed(set, index, ref playerTilePlayed, ref boardTileNotPlayed);
        var child = new RummiNode(set, Tiles, UsedTiles, Jokers, index + 1, LeafNodes,
            Score + set.GetScore(), this, isRun, _isPlayerTile, _boardJokers, _totalJoker,
            playerTilePlayed, boardTileNotPlayed);
        MarkTilesAsUnused(set, index);
        Children.Add(child);
    }

    private void MarkTilesAsUsed(ValidSet set, int anchorIndex, ref int playerTilePlayed, ref int boardTileNotPlayed)
    {
        var anchorSkipped = false;
        var searchFrom = anchorIndex + 1;

        foreach (var tile in set.Tiles)
        {
            if (tile.IsJoker)
            {
                Jokers -= 1;
                continue;
            }

            // The caller already counted the tile this run grew from. A joker extending
            // the low end sits in front of that anchor, so skip it wherever it lies.
            if (!anchorSkipped && tile.Equals(Tiles[anchorIndex]))
            {
                anchorSkipped = true;
                continue;
            }

            for (; searchFrom < Tiles.Length; searchFrom++)
            {
                if (UsedTiles[searchFrom] || !Tiles[searchFrom].Equals(tile)) continue;

                if (_isPlayerTile[searchFrom]) playerTilePlayed++;
                else boardTileNotPlayed--;

                UsedTiles[searchFrom] = true;
                break;
            }
        }
    }

    public Solution GetSolution()
    {
        var solution = new Solution { IsValid = true };
        var currentNode = this;

        while (currentNode._parentNode is not null)
        {
            if (currentNode._isRun)
                solution.AddRun(currentNode._set);
            else
                solution.AddGroup(currentNode._set);

            currentNode = currentNode._parentNode;
        }

        return solution;
    }
}
