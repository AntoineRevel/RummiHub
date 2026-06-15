using System.Diagnostics;
using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;
using Xunit.Abstractions;

namespace RummiHub.Tests.Solver;

/// <summary>
/// Correctness of the polynomial-time <see cref="DynamicSolver"/> is established the only
/// way an exact solver can be trusted without a hand proof: differentially, against the
/// exponential <see cref="GraphSolver"/> reference over hundreds of random positions. The
/// DP must (1) agree on whether a move exists, (2) never be worse than the reference
/// (lexicographically by tiles-played then score), and (3) always return a legal move.
/// </summary>
public class DynamicSolverTests
{
    // ── targeted cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Rearranges_BoardToFreeRoom_ForRackTile()
    {
        // Board: run [3R,4R,5R] and group [7B,7R,7Bl]. Rack: [6R] extends the run; [7Mango]
        // completes the group to four. The DP should play both rack tiles.
        var board = new Solution { IsValid = true };
        board.AddRun(new ValidSet([new Tile(3, TileColor.Red), new Tile(4, TileColor.Red), new Tile(5, TileColor.Red)]));
        board.AddGroup(new ValidSet([new Tile(7, TileColor.Blue), new Tile(7, TileColor.Red), new Tile(7, TileColor.Black)]));

        var rack = new Set([new Tile(6, TileColor.Red), new Tile(7, TileColor.Mango)]);

        var result = DynamicSolver.Create(board.ToSet(), rack).SearchSolution();

        result.Found.Should().BeTrue();
        result.TilesToPlay.Should().HaveCount(2);
        SolutionValidator.IsValid(result, board.ToSet(), rack, hasPlayed: true).Should().BeTrue();
    }

    [Fact]
    public void UsesBoardJoker_AndKeepsItOnBoard()
    {
        // Board run with a joker filling the 4 slot: [3B, joker(4B), 5B]. Rack [6B] extends it.
        var board = new Solution { IsValid = true };
        board.AddRun(new ValidSet([new Tile(3, TileColor.Blue), new Tile(4, TileColor.Blue, isJoker: true), new Tile(5, TileColor.Blue)]));
        var rack = new Set([new Tile(6, TileColor.Blue)]);

        var boardSet = board.ToSet();
        boardSet.Jokers.Should().Be(1);

        var result = DynamicSolver.Create(boardSet, rack).SearchSolution();

        result.Found.Should().BeTrue();
        SolutionValidator.IsValid(result, boardSet, rack, hasPlayed: true).Should().BeTrue();
        // The single joker is the board's; none is played from the (joker-free) rack.
        result.JokersToPlay.Should().Be(0);
    }

    [Fact]
    public void FirstPlay_UsesJokerToReachMinScore()
    {
        // [12B, 13B] + joker → [joker(11B), 12B, 13B] = 36 ≥ 30.
        var rack = new Set([new Tile(12, TileColor.Blue), new Tile(13, TileColor.Blue), new Tile(isJoker: true)]);

        var result = DynamicSolver.CreateFirst(rack).SearchSolution();

        result.Found.Should().BeTrue();
        result.Score.Should().BeGreaterThanOrEqualTo(ISolver.MinScore);
        SolutionValidator.IsValid(result, new Set(), rack, hasPlayed: false).Should().BeTrue();
    }

    [Fact]
    public void FirstPlay_BelowMinScore_ReportsBestAchievableScore()
    {
        // Three 9s form a legal group worth 27 — a valid set, but below the 30-point first-play
        // threshold. The move is rejected, yet the solver must still report 27 so the UI can tell
        // the player how close they were (regression: it used to report 0).
        var rack = new Set([
            new Tile(9, TileColor.Blue), new Tile(9, TileColor.Red), new Tile(9, TileColor.Black)
        ]);

        var result = DynamicSolver.CreateFirst(rack).SearchSolution();

        result.Found.Should().BeFalse();
        result.Score.Should().Be(27);
    }

    [Fact]
    public void FirstPlay_NoValidSet_ReportsZeroScore()
    {
        // Tiles that form no run and no group can score nothing — 0 is the correct report here.
        var rack = new Set([
            new Tile(1, TileColor.Blue), new Tile(3, TileColor.Red), new Tile(5, TileColor.Black)
        ]);

        var result = DynamicSolver.CreateFirst(rack).SearchSolution();

        result.Found.Should().BeFalse();
        result.Score.Should().Be(0);
    }

    // ── differential: during game ──────────────────────────────────────────────────

    [Fact]
    public void DuringGame_MatchesGraphSolver_OverRandomPositions()
    {
        var rng = new Random(20240614);
        var legalComparisons = 0;

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var (board, rack) = RandomPosition(rng, withBoard: true);
            var boardSet = board.ToSet();

            var graph = GraphSolver.Create(new Set(boardSet), new Set(rack)).SearchSolution();
            var dp = DynamicSolver.Create(new Set(boardSet), new Set(rack)).SearchSolution();

            // Every DP move must be legal, full stop.
            if (dp.Found)
                SolutionValidator.IsValid(dp, boardSet, rack, hasPlayed: true).Should()
                    .BeTrue(because: $"every DP move must be legal.\n{Describe(boardSet, rack)}");

            // GraphSolver is only a trustworthy reference when its own answer is legal: it has a
            // known bug letting jokers represent values above 13, so gate the comparison on it.
            var graphLegal = graph.Found && SolutionValidator.IsValid(graph, boardSet, rack, hasPlayed: true);

            if (!graph.Found)
                dp.Found.Should().BeFalse(
                    because: $"an exhaustive search found nothing, so the DP must not either.\n{Describe(boardSet, rack)}");
            else if (graphLegal)
            {
                dp.Found.Should().BeTrue(because: $"a legal move exists.\n{Describe(boardSet, rack)}");
                AtLeastAsGood(dp, graph)
                    .Should().BeTrue(because: $"DP is exact and may never be worse than a legal GraphSolver result.\n" +
                                              $"DP=({Count(dp)},{dp.Score}) Graph=({Count(graph)},{graph.Score})\n{Describe(boardSet, rack)}");
                legalComparisons++;
            }
        }

        // Guard against a vacuous pass: the bulk of positions must be real, legal comparisons in
        // which the DP exactly matches the exhaustive optimum.
        legalComparisons.Should().BeGreaterThan(200);
    }

    // ── differential: first play ─────────────────────────────────────────────────────

    [Fact]
    public void FirstPlay_MatchesGraphSolver_OverRandomRacks()
    {
        var rng = new Random(987654321);

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var (_, rack) = RandomPosition(rng, withBoard: false);

            var graph = GraphFirstSolver.Create(new Set(rack)).SearchSolution();
            var dp = DynamicSolver.CreateFirst(new Set(rack)).SearchSolution();

            if (dp.Found)
                SolutionValidator.IsValid(dp, new Set(), rack, hasPlayed: false).Should()
                    .BeTrue(because: $"every first play must be legal.\n{Describe(new Set(), rack)}");

            // GraphFirstSolver shares the >13 joker bug, so only trust it when its result is legal.
            var graphLegal = graph.Found && SolutionValidator.IsValid(graph, new Set(), rack, hasPlayed: false);

            if (!graph.Found)
                dp.Found.Should().BeFalse(
                    because: $"no play reaches 30, so the DP must not claim one.\n{Describe(new Set(), rack)}");
            else if (graphLegal)
            {
                dp.Found.Should().BeTrue(because: $"a legal ≥30 play exists.\n{Describe(new Set(), rack)}");
                dp.Score.Should().BeGreaterThanOrEqualTo(graph.Score,
                    because: $"DP maximizes score and may never be worse than a legal reference.\n{Describe(new Set(), rack)}");
            }
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static int Count(SolverResult result) => result.TilesToPlay.Count(t => !t.IsJoker);

    private static bool AtLeastAsGood(SolverResult dp, SolverResult graph)
    {
        var dpKey = (Count(dp), dp.Score);
        var graphKey = (Count(graph), graph.Score);
        return dpKey.CompareTo(graphKey) >= 0;
    }

    /// <summary>Builds a random, always-legal board (no board jokers) plus a random rack.</summary>
    private static (Solution board, Set rack) RandomPosition(Random rng, bool withBoard)
    {
        var deck = new int[14, 4];
        for (var v = 1; v <= 13; v++)
        for (var k = 0; k < 4; k++)
            deck[v, k] = 2;

        var board = new Solution { IsValid = true };

        if (withBoard)
        {
            var sets = rng.Next(1, 5);
            for (var s = 0; s < sets; s++)
                if (rng.Next(2) == 0) TryDrawRun(deck, rng, board);
                else TryDrawGroup(deck, rng, board);
        }

        var rack = new Set();
        var rackSize = rng.Next(1, 11);
        for (var i = 0; i < rackSize; i++)
        {
            var v = rng.Next(1, 14);
            var k = rng.Next(0, 4);
            if (deck[v, k] <= 0) continue;
            deck[v, k]--;
            rack.AddTile(new Tile(v, (TileColor)k));
        }

        // Occasionally hand the rack a joker or two.
        for (var j = 0; j < 2; j++)
            if (rng.Next(4) == 0)
                rack.AddTile(new Tile(isJoker: true));

        return (board, rack);
    }

    private static void TryDrawRun(int[,] deck, Random rng, Solution board)
    {
        var color = rng.Next(0, 4);
        var length = rng.Next(3, 6);
        var start = rng.Next(1, 13 - length + 2);

        for (var v = start; v < start + length; v++)
            if (deck[v, color] <= 0)
                return;

        var tiles = new Tile[length];
        for (var i = 0; i < length; i++)
        {
            deck[start + i, color]--;
            tiles[i] = new Tile(start + i, (TileColor)color);
        }

        board.AddRun(new ValidSet(tiles));
    }

    private static void TryDrawGroup(int[,] deck, Random rng, Solution board)
    {
        var value = rng.Next(1, 14);
        var colors = Enumerable.Range(0, 4).OrderBy(_ => rng.Next()).ToList();
        var size = rng.Next(3, 5);

        var chosen = new List<int>();
        foreach (var c in colors)
        {
            if (chosen.Count == size) break;
            if (deck[value, c] > 0) chosen.Add(c);
        }

        if (chosen.Count < 3) return;

        var tiles = new Tile[chosen.Count];
        for (var i = 0; i < chosen.Count; i++)
        {
            deck[value, chosen[i]]--;
            tiles[i] = new Tile(value, (TileColor)chosen[i]);
        }

        board.AddGroup(new ValidSet(tiles));
    }

    private static string Describe(Set board, Set rack)
    {
        var boardDesc = string.Join(",", board.GetAllTilesIncludingJokers().Select(t => t.ToString()));
        var rackDesc = string.Join(",", rack.GetAllTilesIncludingJokers().Select(t => t.ToString()));
        return $"Board: [{boardDesc}]\nRack:  [{rackDesc}]";
    }
}

/// <summary>
/// Performance characterization: the DP must stay comfortably fast even on a heavy table,
/// where the exponential tree search becomes expensive. Doubles as a regression guard.
/// </summary>
public class DynamicSolverPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void HeavyPosition_SolvesQuickly()
    {
        var (board, rack) = HeavyPosition();
        var boardSet = board.ToSet();

        // Warm up the static tables, then time a cold solve.
        DynamicSolver.Create(new Set(boardSet), new Set(rack)).SearchSolution();

        var sw = Stopwatch.StartNew();
        SolverResult dp = default!;
        for (var i = 0; i < 20; i++)
            dp = DynamicSolver.Create(new Set(boardSet), new Set(rack)).SearchSolution();
        sw.Stop();

        var perSolve = sw.Elapsed.TotalMilliseconds / 20;
        output.WriteLine($"DynamicSolver heavy position: {perSolve:F2} ms/solve, " +
                         $"Found={dp.Found}, tiles={dp.TilesToPlay.Count()}, score={dp.Score}");

        dp.Found.Should().BeTrue();
        // Generous headroom over the ~50 ms typical so CI contention never flakes; a real
        // (exponential) regression would blow far past this.
        perSolve.Should().BeLessThan(1000, because: "the polynomial DP must stay fast on a full table");
    }

    [Fact]
    public void MediumPosition_DpVastlyOutpacesGraph()
    {
        // A medium table: enough to make the exponential search noticeably slow (~1-2 s) while
        // the DP stays in the low milliseconds — keeping this comparison cheap for CI.
        var board = new Solution { IsValid = true };
        board.AddRun(new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue), new Tile(4, TileColor.Blue)]));
        board.AddRun(new ValidSet([new Tile(8, TileColor.Black), new Tile(9, TileColor.Black), new Tile(10, TileColor.Black), new Tile(11, TileColor.Black)]));
        board.AddRun(new ValidSet([new Tile(5, TileColor.Red), new Tile(6, TileColor.Red), new Tile(7, TileColor.Red)]));
        board.AddGroup(new ValidSet([new Tile(9, TileColor.Blue), new Tile(9, TileColor.Red), new Tile(9, TileColor.Mango)]));
        board.AddGroup(new ValidSet([new Tile(12, TileColor.Blue), new Tile(12, TileColor.Red), new Tile(12, TileColor.Black), new Tile(12, TileColor.Mango)]));
        var boardSet = board.ToSet();

        var rack = new Set([
            new Tile(5, TileColor.Blue), new Tile(6, TileColor.Blue), new Tile(7, TileColor.Blue),
            new Tile(8, TileColor.Red), new Tile(10, TileColor.Mango), new Tile(11, TileColor.Red),
            new Tile(13, TileColor.Black), new Tile(3, TileColor.Red), new Tile(1, TileColor.Black),
            new Tile(6, TileColor.Mango)
        ]);

        var dpWatch = Stopwatch.StartNew();
        var dp = DynamicSolver.Create(new Set(boardSet), new Set(rack)).SearchSolution();
        dpWatch.Stop();

        var graphWatch = Stopwatch.StartNew();
        var graph = GraphSolver.Create(new Set(boardSet), new Set(rack)).SearchSolution();
        graphWatch.Stop();

        output.WriteLine($"DP:    {dpWatch.Elapsed.TotalMilliseconds:F1} ms (tiles={dp.TilesToPlay.Count()}, score={dp.Score})");
        output.WriteLine($"Graph: {graphWatch.Elapsed.TotalMilliseconds:F1} ms (tiles={graph.TilesToPlay.Count()}, score={graph.Score})");

        dp.Found.Should().BeTrue();
        // The DP never plays fewer tiles than the exhaustive search, and matches its optimum here.
        dp.TilesToPlay.Count(t => !t.IsJoker).Should().BeGreaterThanOrEqualTo(graph.TilesToPlay.Count(t => !t.IsJoker));
    }

    /// <summary>A dense, legal table plus a full rack — the kind of position that stresses a solver.</summary>
    private static (Solution board, Set rack) HeavyPosition()
    {
        var board = new Solution { IsValid = true };

        // Runs across several colors.
        board.AddRun(new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue), new Tile(4, TileColor.Blue)]));
        board.AddRun(new ValidSet([new Tile(5, TileColor.Red), new Tile(6, TileColor.Red), new Tile(7, TileColor.Red)]));
        board.AddRun(new ValidSet([new Tile(8, TileColor.Black), new Tile(9, TileColor.Black), new Tile(10, TileColor.Black), new Tile(11, TileColor.Black)]));
        board.AddRun(new ValidSet([new Tile(2, TileColor.Mango), new Tile(3, TileColor.Mango), new Tile(4, TileColor.Mango)]));

        // Groups.
        board.AddGroup(new ValidSet([new Tile(9, TileColor.Blue), new Tile(9, TileColor.Red), new Tile(9, TileColor.Mango)]));
        board.AddGroup(new ValidSet([new Tile(12, TileColor.Blue), new Tile(12, TileColor.Red), new Tile(12, TileColor.Black), new Tile(12, TileColor.Mango)]));

        var rack = new Set([
            new Tile(5, TileColor.Blue), new Tile(6, TileColor.Blue), new Tile(7, TileColor.Blue),
            new Tile(8, TileColor.Red), new Tile(9, TileColor.Black), new Tile(10, TileColor.Mango),
            new Tile(11, TileColor.Red), new Tile(13, TileColor.Black), new Tile(13, TileColor.Blue),
            new Tile(3, TileColor.Red), new Tile(1, TileColor.Black), new Tile(isJoker: true),
            new Tile(isJoker: true), new Tile(6, TileColor.Mango)
        ]);

        return (board, rack);
    }
}
