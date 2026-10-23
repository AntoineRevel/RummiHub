using RummiHub.Core.Models;

namespace RummiHub.Solver.Worker.Solver.Abstract;

public abstract class BaseSolver(Tile[] tiles, int jokers)
{
    protected readonly Tile[] Tiles = tiles;
    protected readonly bool[] UsedTiles = new bool[tiles.Length];
    public int Jokers = jokers;

    protected void MarkTilesAsUsed(ValidSet set, int anchorIndex)
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

            // The caller already marked the tile the run was grown from. A joker may now
            // sit in front of it, so that anchor is skipped wherever it appears.
            if (!anchorSkipped && tile.Equals(Tiles[anchorIndex]))
            {
                anchorSkipped = true;
                continue;
            }

            for (; searchFrom < Tiles.Length; searchFrom++)
            {
                if (UsedTiles[searchFrom] || !Tiles[searchFrom].Equals(tile)) continue;

                UsedTiles[searchFrom] = true;
                break;
            }
        }
    }

    protected void MarkTilesAsUnused(ValidSet set, int anchorIndex)
    {
        var anchorSkipped = false;
        var lastIndex = Tiles.Length - 1;

        for (var tIndex = set.Tiles.Length - 1; tIndex >= 0; tIndex--)
        {
            var tile = set.Tiles[tIndex];

            if (tile.IsJoker)
            {
                Jokers += 1;
                continue;
            }

            if (!anchorSkipped && tile.Equals(Tiles[anchorIndex]))
            {
                anchorSkipped = true;
                continue;
            }

            for (; lastIndex > anchorIndex; lastIndex--)
            {
                if (!UsedTiles[lastIndex] || !Tiles[lastIndex].Equals(tile)) continue;

                UsedTiles[lastIndex] = false;
                break;
            }
        }
    }

    protected void MarkTilesAsUsedOut(ValidSet set, int anchorIndex, out int playerSetScore)
    {
        playerSetScore = 0;
        var anchorSkipped = false;
        var searchFrom = anchorIndex + 1;

        foreach (var tile in set.Tiles)
        {
            if (tile.IsJoker)
            {
                playerSetScore += tile.Value;
                Jokers -= 1;
                continue;
            }

            // The caller adds the anchor's own value. Count every other tile, including a
            // joker placed in front of a run that can no longer grow past 13.
            if (!anchorSkipped && tile.Equals(Tiles[anchorIndex]))
            {
                anchorSkipped = true;
                continue;
            }

            playerSetScore += tile.Value;

            for (; searchFrom < Tiles.Length; searchFrom++)
            {
                if (UsedTiles[searchFrom] || !Tiles[searchFrom].Equals(tile)) continue;

                UsedTiles[searchFrom] = true;
                break;
            }
        }
    }

    protected IEnumerable<ValidSet> GetRuns(int tileIndex, CancellationToken cancellationToken = default)
    {
        var availableJokers = Jokers;
        var color = Tiles[tileIndex].Color;
        var currentRun = new List<Tile> { Tiles[tileIndex] };
        var i = tileIndex + 1;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            for (; i < Tiles.Length; i++)
            {
                if (Tiles[i].Color != color)
                {
                    i = Tiles.Length;
                    break;
                }

                if (UsedTiles[i] || Tiles[i].Value == currentRun[^1].Value) continue;

                if (Tiles[i].Value != currentRun[^1].Value + 1) break;

                currentRun.Add(Tiles[i]);

                if (currentRun.Count < 3) continue;

                yield return new ValidSet([..currentRun]);
            }

            if (availableJokers <= 0) yield break;

            // A joker stands for 1..13 only. Once the high end is 13, spare jokers extend
            // the low end instead (12-13 becomes joker-11, 12, 13) and stop at 1.
            var high = currentRun[^1].Value + 1;
            if (high <= 13)
            {
                currentRun.Add(new Tile(high, color, true));
            }
            else
            {
                var low = currentRun[0].Value - 1;
                if (low < 1) yield break;

                currentRun.Insert(0, new Tile(low, color, true));
            }

            availableJokers--;

            if (currentRun.Count < 3) continue;

            yield return new ValidSet([..currentRun]);
        }
    }

    protected IEnumerable<ValidSet> GetGroups(int firstTileIndex, CancellationToken cancellationToken = default)
    {
        var sameNumberTiles = new HashSet<Tile>();

        for (var i = firstTileIndex; i < Tiles.Length; i++)
            if (!UsedTiles[i] &&
                Tiles[i].Value == Tiles[firstTileIndex].Value &&
                Tiles[i].Color != Tiles[firstTileIndex].Color)
                sameNumberTiles.Add(Tiles[i]);

        var size = sameNumberTiles.Count;

        if (Jokers == 0)
        {
            if (size < 2) yield break;

            var groupTiles = new List<Tile> { Tiles[firstTileIndex] };

            groupTiles.AddRange(sameNumberTiles);

            yield return new ValidSet(groupTiles.ToArray());

            if (groupTiles.Count != 4) yield break;
            var uniqueTilesList = sameNumberTiles.ToList();

            for (var i = 0; i < sameNumberTiles.Count; i++)
            for (var j = i + 1; j < sameNumberTiles.Count; j++)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                yield return new ValidSet([Tiles[firstTileIndex], uniqueTilesList[i], uniqueTilesList[j]]);
            }
        }
        else
        {
            var uniqueTilesList = sameNumberTiles.ToList();
            for (var jokersUsed = 0; jokersUsed <= Jokers; jokersUsed++)
            for (var tilesUsed = 2; tilesUsed <= size + 1; tilesUsed++)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                var totalTiles = tilesUsed + jokersUsed;
                if (totalTiles is < 3 or > 4) continue;

                var combinations = GetCombinations(uniqueTilesList, tilesUsed - 1, cancellationToken);

                foreach (var tiles in combinations)
                {
                    var groupTiles = new Tile[totalTiles];

                    groupTiles[0] = Tiles[firstTileIndex];

                    for (int i = tilesUsed - 2, j = 1; i >= 0; i--, j++) groupTiles[j] = tiles[i];

                    for (var k = 0; k < jokersUsed; k++)
                        groupTiles[tilesUsed + k] = new Tile(Tiles[firstTileIndex].Value, isJoker: true);

                    yield return new ValidSet(groupTiles);
                }
            }
        }
    }

    internal static IEnumerable<List<Tile>> GetCombinations(List<Tile> list, int length,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            yield break;

        if (length == 0) yield return [];

        for (var i = 0; i < list.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;

            var element = list[i];
            foreach (var combination in GetCombinations(list.Skip(i + 1).ToList(), length - 1, cancellationToken))
            {
                combination.Add(element);
                yield return combination;
            }
        }
    }
}
