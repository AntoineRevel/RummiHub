using System.Diagnostics;
using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Strategies;

namespace RummiHub.Tests.Solver;

/// <summary>
/// <see cref="DynamicStrategy"/> trusts the exact DP's verdict, "no move" included, and keeps
/// the exponential <see cref="GraphStrategy"/> only as a safety net for a found move that fails
/// validation or an engine that throws. A spy stands in for the fallback so "never invoked" is
/// observed directly instead of being inferred from timing.
/// </summary>
public class DynamicStrategyTests
{
    private static Tile T(int value, TileColor color) => new(value, color);
    private static ValidSet VSet(params Tile[] tiles) => new(tiles);

    // ── real engine: its verdict is final ─────────────────────────────────────────

    [Fact]
    public async Task NoLegalPlay_LargeRackOnCrowdedTable_ReturnsNotFoundQuickly_WithoutFallback()
    {
        // Not one of the 24 rack tiles can be laid down on this crowded table. The DP settles that
        // in a fraction of a second; GraphSolver took ~25 s with only the four tiles the player
        // really held there, and ran out of memory (16 GB) on the full rack.
        var (board, rack) = CrowdedTableWithNoPlay();
        var fallback = new SpyFallback();

        // Warm up the DP's static tables and JIT so the stopwatch only sees the solve itself.
        DynamicSolver.Create(board.ToSet(), new Set(rack)).SearchSolution();

        var stopwatch = Stopwatch.StartNew();
        var result = await new DynamicStrategy(fallback).GetSolverResult(board.ToSet(), rack, hasPlayed: true);
        stopwatch.Stop();

        result.Found.Should().BeFalse();
        result.Source.Should().Be(nameof(DynamicSolver));
        fallback.Calls.Should().Be(0, "an exact \"no move\" must never be re-searched by the exponential fallback");
        // Generous bound (the DP takes ~0.1 s here, ~0.5 s under parallel test load) that still sits
        // far below the tens of seconds the exponential search spends on this table.
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FirstPlayBelowMinScore_ReturnsNotFoundWithBestScore_WithoutFallback()
    {
        // Three 9s: a legal group worth 27, short of the 30-point opening threshold.
        var rack = new Set([T(9, TileColor.Blue), T(9, TileColor.Red), T(9, TileColor.Black)]);
        var fallback = new SpyFallback();

        var result = await new DynamicStrategy(fallback).GetSolverResult(new Set(), rack, hasPlayed: false);

        result.Found.Should().BeFalse();
        result.Score.Should().Be(27, "the best reachable score is still reported so the UI can show it");
        result.Source.Should().Be(nameof(DynamicSolver));
        fallback.Calls.Should().Be(0);
    }

    [Fact]
    public async Task NormalPlay_ReturnsTheDynamicResult_WithoutFallback()
    {
        // Board run [10B, 11B, 12B] + rack [13B] → extend the run.
        var board = new Set([T(10, TileColor.Blue), T(11, TileColor.Blue), T(12, TileColor.Blue)]);
        var rack = new Set([T(13, TileColor.Blue)]);
        var fallback = new SpyFallback();

        var result = await new DynamicStrategy(fallback).GetSolverResult(board, rack, hasPlayed: true);

        result.Found.Should().BeTrue();
        result.Source.Should().Be(nameof(DynamicSolver));
        result.TilesToPlay.Should().Equal(T(13, TileColor.Blue));
        fallback.Calls.Should().Be(0);
    }

    // ── safety net: kept for a bad move or a crashing engine ─────────────────────────

    [Fact]
    public async Task FoundMoveFailingValidation_FallsBack()
    {
        // The engine claims a move, but its "run" is only two tiles long — illegal.
        var illegal = new Solution { IsValid = true };
        illegal.AddRun(VSet(T(12, TileColor.Blue), T(13, TileColor.Blue)));
        var bogus = SolverResult.FromSolution("Stub", illegal, [T(13, TileColor.Blue)], score: 25);
        var fallback = new SpyFallback();

        var result = await new DynamicStrategy(fallback, Engine(() => bogus))
            .GetSolverResult(new Set([T(12, TileColor.Blue)]), new Set([T(13, TileColor.Blue)]), hasPlayed: true);

        result.Should().BeSameAs(fallback.Answer);
        fallback.Calls.Should().Be(1);
    }

    [Fact]
    public async Task EngineThrowing_FallsBack()
    {
        var fallback = new SpyFallback();

        var result = await new DynamicStrategy(fallback, Engine(() => throw new InvalidOperationException("engine bug")))
            .GetSolverResult(new Set(), new Set([T(13, TileColor.Blue)]), hasPlayed: true);

        result.Should().BeSameAs(fallback.Answer);
        fallback.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_IsRethrown_WithoutFallback()
    {
        var fallback = new SpyFallback();
        var strategy = new DynamicStrategy(fallback, Engine(() => throw new OperationCanceledException()));

        var act = () => strategy.GetSolverResult(new Set(), new Set([T(13, TileColor.Blue)]), hasPlayed: true);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fallback.Calls.Should().Be(0);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static Func<Set, Set, bool, ISolver> Engine(Func<SolverResult> search) =>
        (_, _, _) => new StubSolver(search);

    private sealed class StubSolver(Func<SolverResult> search) : ISolver
    {
        public SolverResult SearchSolution(CancellationToken cancellationToken = default) => search();
    }

    /// <summary>Counts its calls and answers with a result no engine produces.</summary>
    private sealed class SpyFallback : IStrategy
    {
        public SolverResult Answer { get; } = SolverResult.Invalid(nameof(SpyFallback));

        public int Calls { get; private set; }

        public Task<SolverResult> GetSolverResult(Set board, Set rack, bool hasPlayed,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Answer);
        }
    }

    /// <summary>
    /// A real mid-game table from a seeded trace (43 tiles in 13 sets, one joker) and a 24-tile
    /// rack: the four tiles the player actually held there (1B, 8M, 6Bk, 11Bk) plus twenty more
    /// still off the table, none of which can be played, even by rearranging the table.
    /// </summary>
    private static (Solution board, Set rack) CrowdedTableWithNoPlay()
    {
        var board = new Solution { IsValid = true };
        board.AddRun(VSet(T(4, TileColor.Blue), T(5, TileColor.Blue), T(6, TileColor.Blue)));
        board.AddRun(VSet(T(3, TileColor.Mango), T(4, TileColor.Mango), T(5, TileColor.Mango), T(6, TileColor.Mango)));
        board.AddRun(VSet(T(4, TileColor.Black), T(5, TileColor.Black), T(6, TileColor.Black), T(7, TileColor.Black)));
        board.AddRun(VSet(T(7, TileColor.Blue), T(8, TileColor.Blue), T(9, TileColor.Blue)));
        board.AddRun(VSet(T(11, TileColor.Red), T(12, TileColor.Red), T(13, TileColor.Red)));
        board.AddRun(VSet(T(9, TileColor.Red), T(10, TileColor.Red), T(11, TileColor.Red), T(12, TileColor.Red), T(13, TileColor.Red)));
        board.AddGroup(VSet(T(1, TileColor.Blue), T(1, TileColor.Red), T(1, TileColor.Black)));
        board.AddGroup(VSet(T(2, TileColor.Blue), T(2, TileColor.Red), T(2, TileColor.Black)));
        board.AddGroup(VSet(new Tile(2, TileColor.Blue, isJoker: true), T(2, TileColor.Red), T(2, TileColor.Black)));
        board.AddGroup(VSet(T(3, TileColor.Red), T(3, TileColor.Mango), T(3, TileColor.Black)));
        board.AddGroup(VSet(T(7, TileColor.Red), T(7, TileColor.Mango), T(7, TileColor.Black)));
        board.AddGroup(VSet(T(8, TileColor.Blue), T(8, TileColor.Red), T(8, TileColor.Mango)));
        board.AddGroup(VSet(T(11, TileColor.Blue), T(11, TileColor.Mango), T(11, TileColor.Black)));

        var rack = new Set([
            T(1, TileColor.Blue), T(4, TileColor.Blue), T(9, TileColor.Blue), T(11, TileColor.Blue), T(12, TileColor.Blue), T(12, TileColor.Blue),
            T(3, TileColor.Red), T(5, TileColor.Red), T(5, TileColor.Red), T(7, TileColor.Red), T(9, TileColor.Red),
            T(5, TileColor.Mango), T(6, TileColor.Mango), T(8, TileColor.Mango), T(10, TileColor.Mango), T(10, TileColor.Mango), T(13, TileColor.Mango), T(13, TileColor.Mango),
            T(1, TileColor.Black), T(4, TileColor.Black), T(6, TileColor.Black), T(11, TileColor.Black), T(12, TileColor.Black), T(12, TileColor.Black)
        ]);

        return (board, rack);
    }
}
