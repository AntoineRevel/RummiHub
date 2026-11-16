using AwesomeAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;
using Xunit.Abstractions;

namespace RummiHub.Tests.Solver;

/// <summary>
/// Whole games played by the solver, the board laid out on the grid move after move, on a
/// desktop-wide and a phone-wide table.
/// </summary>
public class BoardGridGameTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(44)] // a desktop
    [InlineData(17)] // a phone
    public void Grid_NeverOverlapsSets_AndMovesOnlyWhatTheMoveChanges(int columns)
    {
        var moves = 0;
        var gridShifted = 0;
        var flowShifted = 0;
        var relocated = 0;
        var maxRows = 0;

        for (var seed = 1; seed <= 30; seed++)
        {
            IReadOnlyList<ValidSet> previous = [];
            BoardSpot[] spots = [];

            foreach (var next in PlayGame(seed, seed % 2 == 0 ? 2 : 4))
            {
                var placed = BoardGrid.Place(previous, spots, next, columns);

                AssertNoOverlap(next, placed, columns);
                // By tiles, not by instance: a twin set played this move may take the name of
                // the one already there, the table looks the same.
                var staying = next.Where(set => previous.Contains(set, ReferenceEqualityComparer.Instance)).ToList();
                Cells(staying, previous, spots).Should().BeSubsetOf(Cells(next.ToList(), next, placed),
                    "a set the move leaves alone stays where it is");

                moves++;
                gridShifted += Shifted(previous, spots, next, placed);
                flowShifted += Shifted(previous, Flow(previous, columns), next, Flow(next, columns));
                relocated += Relocated(previous, spots, next, placed);
                maxRows = Math.Max(maxRows, placed.Max(spot => spot.Row) + 1);

                previous = next;
                spots = placed;
            }
        }

        output.WriteLine($"columns={columns} moves={moves} kept tiles changing cell per move: " +
                         $"grid={(double)gridShifted / moves:F2} list={(double)flowShifted / moves:F2}; " +
                         $"sets relocated per move={(double)relocated / moves:F3}; max rows={maxRows}");
        gridShifted.Should().BeLessThan(flowShifted / 4);
    }

    private static void AssertNoOverlap(IReadOnlyList<ValidSet> sets, BoardSpot[] spots, int columns)
    {
        for (var a = 0; a < sets.Count; a++)
        {
            spots[a].Col.Should().BeGreaterThanOrEqualTo(0);
            (spots[a].Col + BoardGrid.Width(sets[a])).Should()
                .BeLessThanOrEqualTo(Math.Max(columns, BoardGrid.Width(sets[a])));
            for (var b = a + 1; b < sets.Count; b++)
            {
                if (spots[a].Row != spots[b].Row) continue;
                var apart = spots[a].Col < spots[b].Col
                    ? spots[b].Col - (spots[a].Col + BoardGrid.Width(sets[a]))
                    : spots[a].Col - (spots[b].Col + BoardGrid.Width(sets[b]));
                apart.Should().BeGreaterThanOrEqualTo(BoardGrid.Gap);
            }
        }
    }

    /// <summary>The spots of <paramref name="which"/> among <paramref name="sets"/>, as sorted text.</summary>
    private static List<string> Cells(List<ValidSet> which, IReadOnlyList<ValidSet> sets, BoardSpot[] spots)
    {
        var cells = new List<string>();
        for (var i = 0; i < sets.Count; i++)
            if (which.Contains(sets[i], ReferenceEqualityComparer.Instance))
                cells.Add($"{string.Join(' ', sets[i].Tiles)} @ {spots[i]}");
        cells.Sort(StringComparer.Ordinal);
        return cells;
    }

    /// <summary>Tiles staying in their set (see <see cref="BoardSeating.Pair"/>) that change cell.</summary>
    private static int Shifted(IReadOnlyList<ValidSet> previous, BoardSpot[] before,
        IReadOnlyList<ValidSet> next, BoardSpot[] after)
    {
        var pairs = BoardSeating.Pair(previous, next);
        var shifted = 0;
        for (var j = 0; j < next.Count; j++)
        {
            var i = pairs[j].PrevIndex;
            if (i < 0) continue;
            var seats = previous[i].Tiles.Select((tile, seat) => (tile, seat)).ToList();
            for (var b = 0; b < next[j].Tiles.Length; b++)
            {
                var at = seats.FindIndex(entry => Same(entry.tile, next[j].Tiles[b]));
                if (at < 0) continue;
                var seat = seats[at].seat;
                seats.RemoveAt(at);
                if (before[i].Row != after[j].Row
                    || before[i].Col + BoardGrid.TileWidth * seat != after[j].Col + BoardGrid.TileWidth * b)
                    shifted++;
            }
        }

        return shifted;
    }

    /// <summary>Sets keeping tiles of a previous set none of which stays on its cell.</summary>
    private static int Relocated(IReadOnlyList<ValidSet> previous, BoardSpot[] before,
        IReadOnlyList<ValidSet> next, BoardSpot[] after)
    {
        var pairs = BoardSeating.Pair(previous, next);
        var relocated = 0;
        for (var j = 0; j < next.Count; j++)
            if (pairs[j].PrevIndex >= 0 && before[pairs[j].PrevIndex].Row != after[j].Row)
                relocated++;
        return relocated;
    }

    private static bool Same(Tile a, Tile b) => a.IsJoker ? b.IsJoker : !b.IsJoker && a.Equals(b);

    /// <summary>The list layout the board had before the grid: sets one after the other, wrapping.</summary>
    private static BoardSpot[] Flow(IReadOnlyList<ValidSet> sets, int columns)
    {
        var spots = new BoardSpot[sets.Count];
        int row = 0, col = 0;
        for (var i = 0; i < sets.Count; i++)
        {
            var width = BoardGrid.Width(sets[i]);
            if (col > 0 && col + width > columns) (row, col) = (row + 1, 0);
            spots[i] = new BoardSpot(row, col);
            col += width + BoardGrid.Gap;
        }

        return spots;
    }

    /// <summary>The boards of one game after each move that changes it, as the site shows them.</summary>
    private static IEnumerable<IReadOnlyList<ValidSet>> PlayGame(int seed, int players)
    {
        var rng = new Random(seed);
        var tiles = new List<Tile>();
        for (var copy = 0; copy < 2; copy++)
        {
            foreach (var color in Enum.GetValues<TileColor>())
                for (var value = 1; value <= 13; value++)
                    tiles.Add(new Tile(value, color));
            tiles.Add(new Tile(isJoker: true));
        }

        for (var i = tiles.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }

        var racks = Enumerable.Range(0, players).Select(p => new Set(tiles.Skip(p * 14).Take(14).ToList())).ToArray();
        var pool = new Queue<Tile>(tiles.Skip(players * 14));
        var hasPlayed = new bool[players];
        var board = new Solution { IsValid = true };
        var idle = 0;

        for (var turn = 0; turn < 400 && idle < players; turn++)
        {
            var p = turn % players;
            var rack = racks[p];
            ISolver solver = hasPlayed[p]
                ? DynamicSolver.Create(board.ToSet(), new Set(rack))
                : DynamicSolver.CreateFirst(new Set(rack));
            var result = solver.SearchSolution();

            if (!result.Found)
            {
                if (pool.Count > 0) { rack.AddTile(pool.Dequeue()); idle = 0; }
                else idle++;
                continue;
            }

            idle = 0;
            if (hasPlayed[p]) result = BoardStabilizer.Stabilize(board, result);
            foreach (var tile in result.TilesToPlay) rack.Remove(tile);
            for (var i = 0; i < result.JokersToPlay; i++) rack.Remove(new Tile(isJoker: true));

            var sets = hasPlayed[p] ? result.BestSolution.Sets : [.. result.BestSolution.Sets, .. board.Sets];
            hasPlayed[p] = true;
            board = new Solution { IsValid = true, Sets = BoardSeating.Seat(board.Sets, sets) };
            yield return board.Sets;

            if (rack.Tiles.Count == 0 && rack.Jokers == 0) yield break;
        }
    }
}
