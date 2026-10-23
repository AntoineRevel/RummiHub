using RummiHub.Core.Models;

namespace RummiHub.Solver.Worker.Solver.Abstract;

public abstract class ComplexSolver(Tile[] tiles, int jokers, bool[] isPlayerTile) : BaseSolver(tiles, jokers)
{
    protected readonly bool[] IsPlayerTile = isPlayerTile;

    protected void MarkTilesAsUsedOut(ValidSet set, int anchorIndex, int boardJokers, out int playerSetScore)
    {
        playerSetScore = 0;
        var anchorSkipped = false;
        var searchFrom = anchorIndex + 1;

        foreach (var tile in set.Tiles)
        {
            if (tile.IsJoker)
            {
                Jokers -= 1;
                if (boardJokers == 0) playerSetScore++;
                else boardJokers--;
                continue;
            }

            if (!anchorSkipped && tile.Equals(Tiles[anchorIndex]))
            {
                anchorSkipped = true;
                continue;
            }

            for (; searchFrom < Tiles.Length; searchFrom++)
            {
                if (UsedTiles[searchFrom] || !Tiles[searchFrom].Equals(tile)) continue;

                UsedTiles[searchFrom] = true;

                if (IsPlayerTile[searchFrom]) playerSetScore += tile.Value;

                break;
            }
        }
    }
}
