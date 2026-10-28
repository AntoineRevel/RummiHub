using AwesomeAssertions;
using RummiHub.Core.Models;
using RummiHub.Solver.Worker.Solver.Abstract;

namespace RummiHub.Tests.Solver;

/// <summary>
/// Concrete subclass to expose protected members for testing.
/// </summary>
internal class TestSolver(Tile[] tiles, int jokers) : BaseSolver(tiles, jokers)
{
    public IEnumerable<ValidSet> PublicGetRuns(int tileIndex, CancellationToken ct = default)
        => GetRuns(tileIndex, ct);

    public IEnumerable<ValidSet> PublicGetGroups(int firstTileIndex, CancellationToken ct = default)
        => GetGroups(firstTileIndex, ct);
}

public class BaseSolverTests
{
    // ── GetCombinations ───────────────────────────────────────────────────────

    [Fact]
    public void GetCombinations_Length0_ReturnsOneEmptyList()
    {
        var result = BaseSolver.GetCombinations([], 0).ToList();

        result.Should().HaveCount(1);
        result[0].Should().BeEmpty();
    }

    [Fact]
    public void GetCombinations_Length1_ReturnsSingleElements()
    {
        var tiles = new List<Tile> { new(1, TileColor.Blue), new(2, TileColor.Red) };

        var result = BaseSolver.GetCombinations(tiles, 1).ToList();

        result.Should().HaveCount(2);
        result.Should().Contain(c => c.Count == 1);
    }

    [Fact]
    public void GetCombinations_Length2_ReturnsCorrectPairCount()
    {
        var tiles = new List<Tile>
        {
            new(1, TileColor.Blue),
            new(2, TileColor.Red),
            new(3, TileColor.Black)
        };

        var result = BaseSolver.GetCombinations(tiles, 2).ToList();

        // C(3,2) = 3 pairs
        result.Should().HaveCount(3);
        result.Should().AllSatisfy(c => c.Should().HaveCount(2));
    }

    [Fact]
    public void GetCombinations_EmptyList_ReturnsEmpty()
    {
        var result = BaseSolver.GetCombinations([], 1).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetCombinations_Cancelled_ReturnsEmpty()
    {
        var tiles = new List<Tile> { new(1, TileColor.Blue), new(2, TileColor.Red) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = BaseSolver.GetCombinations(tiles, 1, cts.Token).ToList();

        result.Should().BeEmpty();
    }

    // ── GetRuns ───────────────────────────────────────────────────────────────

    [Fact]
    public void GetRuns_ThreeConsecutiveSameColor_ReturnsOneRun()
    {
        // Tiles must be sorted: same color, ascending value
        var tiles = new[]
        {
            new Tile(1, TileColor.Blue),
            new Tile(2, TileColor.Blue),
            new Tile(3, TileColor.Blue)
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().HaveCount(1);
        runs[0].Tiles.Should().HaveCount(3);
    }

    [Fact]
    public void GetRuns_FourConsecutive_ReturnsTwoRuns()
    {
        var tiles = new[]
        {
            new Tile(1, TileColor.Red),
            new Tile(2, TileColor.Red),
            new Tile(3, TileColor.Red),
            new Tile(4, TileColor.Red)
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var runs = solver.PublicGetRuns(0).ToList();

        // Yields run of 3 then run of 4
        runs.Should().HaveCount(2);
        runs.Should().Contain(r => r.Tiles.Length == 3);
        runs.Should().Contain(r => r.Tiles.Length == 4);
    }

    [Fact]
    public void GetRuns_WithJoker_FillsGap()
    {
        // Blue 1, Blue 3 — gap at 2 filled by joker
        var tiles = new[]
        {
            new Tile(1, TileColor.Blue),
            new Tile(3, TileColor.Blue)
        };
        var solver = new TestSolver(tiles, jokers: 1);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().HaveCount(1);
        runs[0].Tiles.Should().HaveCount(3);
        runs[0].Tiles.Should().Contain(t => t.IsJoker);
    }

    [Fact]
    public void GetRuns_LessThanThreeTiles_ReturnsEmpty()
    {
        var tiles = new[]
        {
            new Tile(1, TileColor.Blue),
            new Tile(2, TileColor.Blue)
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().BeEmpty();
    }

    [Fact]
    public void GetRuns_JokerAfter13_ExtendsDownward_NeverAbove13OrZero()
    {
        // 11-12-13 + joker. The only legal extension is a joker as 10, in front.
        // Before the cap, GetRuns appended a joker worth 14 and GetScore counted it (50).
        var tiles = new[]
        {
            new Tile(11, TileColor.Blue),
            new Tile(12, TileColor.Blue),
            new Tile(13, TileColor.Blue)
        };
        var solver = new TestSolver(tiles, jokers: 1);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().Contain(r => r.Tiles.Length == 4);
        runs.SelectMany(r => r.Tiles).Should().OnlyContain(t => t.Value >= 1 && t.Value <= 13);
        var extended = runs.Single(r => r.Tiles.Length == 4);
        extended.Tiles.Where(t => t.IsJoker).Should().ContainSingle().Which.Value.Should().Be(10);
        extended.GetScore().Should().Be(46);
    }

    [Fact]
    public void GetRuns_ShortRunEndingAt13_JokerFillsTheLowEnd()
    {
        // 12-13 is not a run yet. One joker makes [joker 11, 12, 13], not [12, 13, joker 14].
        var tiles = new[]
        {
            new Tile(12, TileColor.Red),
            new Tile(13, TileColor.Red)
        };
        var solver = new TestSolver(tiles, jokers: 1);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().ContainSingle();
        runs[0].Tiles.Select(t => t.Value).Should().Equal(11, 12, 13);
        runs[0].Tiles[0].IsJoker.Should().BeTrue();
        runs[0].GetScore().Should().Be(36);
    }

    [Fact]
    public void GetRuns_TwoJokersOn13_StayInside1To13()
    {
        var tiles = new[] { new Tile(13, TileColor.Black) };
        var solver = new TestSolver(tiles, jokers: 2);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().ContainSingle();
        runs[0].Tiles.Select(t => t.Value).Should().Equal(11, 12, 13);
        runs[0].Tiles.Count(t => t.IsJoker).Should().Be(2);
        runs[0].GetScore().Should().Be(36);
    }

    [Fact]
    public void GetRuns_JokerCannotExtendBelowOne()
    {
        // Upward is still open, and downward from 1 would be the illegal value 0.
        var tiles = new[]
        {
            new Tile(1, TileColor.Mango),
            new Tile(2, TileColor.Mango)
        };
        var solver = new TestSolver(tiles, jokers: 2);

        var runs = solver.PublicGetRuns(0).ToList();

        runs.Should().NotBeEmpty();
        runs.SelectMany(r => r.Tiles).Should().OnlyContain(t => t.Value >= 1 && t.Value <= 13);
        runs.SelectMany(r => r.Tiles).Where(t => t.IsJoker).Should().OnlyContain(t => t.Value == 3 || t.Value == 4);
    }

    [Fact]
    public void GetRuns_DifferentColorBreaksRun()
    {
        // Blue 1, 2, 3 but Red comes between Blue 2 and 3 — since tiles are iterated sequentially
        // and different color causes the loop to set i = Tiles.Length, stopping enumeration
        var tiles = new[]
        {
            new Tile(1, TileColor.Blue),
            new Tile(2, TileColor.Blue),
            new Tile(3, TileColor.Red),   // different color — stops the blue run
            new Tile(3, TileColor.Blue)
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var runs = solver.PublicGetRuns(0).ToList();

        // Blue 1, 2 found, then Red 3 breaks the color; no 3-tile run → empty
        runs.Should().BeEmpty();
    }

    // ── GetGroups ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetGroups_ThreeDifferentColors_ReturnsOneGroup()
    {
        var tiles = new[]
        {
            new Tile(5, TileColor.Blue),
            new Tile(5, TileColor.Red),
            new Tile(5, TileColor.Black)
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var groups = solver.PublicGetGroups(0).ToList();

        groups.Should().HaveCount(1);
        groups[0].Tiles.Should().HaveCount(3);
    }

    [Fact]
    public void GetGroups_FourDifferentColors_ReturnsFourGroups()
    {
        // 4-tile group + C(3,2)=3 three-tile subgroups = 4 total
        var tiles = new[]
        {
            new Tile(7, TileColor.Blue),
            new Tile(7, TileColor.Red),
            new Tile(7, TileColor.Black),
            new Tile(7, TileColor.Mango)
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var groups = solver.PublicGetGroups(0).ToList();

        groups.Should().HaveCount(4);
        groups.Should().Contain(g => g.Tiles.Length == 4);
        groups.Count(g => g.Tiles.Length == 3).Should().Be(3);
    }

    [Fact]
    public void GetGroups_WithJoker_ReturnsGroupContainingJoker()
    {
        // Blue 5, Red 5, and 1 joker → valid 3-tile group
        var tiles = new[]
        {
            new Tile(5, TileColor.Blue),
            new Tile(5, TileColor.Red)
        };
        var solver = new TestSolver(tiles, jokers: 1);

        var groups = solver.PublicGetGroups(0).ToList();

        groups.Should().NotBeEmpty();
        groups.Should().Contain(g => g.Tiles.Any(t => t.IsJoker));
    }

    [Fact]
    public void GetGroups_OnlyOneTileOfSameValue_ReturnsEmpty()
    {
        var tiles = new[]
        {
            new Tile(5, TileColor.Blue),
            new Tile(6, TileColor.Red)   // different value — won't form a group
        };
        var solver = new TestSolver(tiles, jokers: 0);

        var groups = solver.PublicGetGroups(0).ToList();

        groups.Should().BeEmpty();
    }
}
