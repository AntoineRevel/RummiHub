using FluentAssertions;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class SolutionTests
{
    [Fact]
    public void AddSolution_MergesRunsAndGroups()
    {
        var a = new Solution();
        a.AddRun(new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]));

        var b = new Solution();
        b.AddGroup(new ValidSet([new Tile(5, TileColor.Blue), new Tile(5, TileColor.Red), new Tile(5, TileColor.Black)]));

        a.AddSolution(b);

        a.Runs.Should().HaveCount(1);
        a.Groups.Should().HaveCount(1);
    }

    [Fact]
    public void ToSet_FlattensTiles()
    {
        var solution = new Solution();
        solution.AddRun(new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]));
        solution.AddGroup(new ValidSet([new Tile(7, TileColor.Red), new Tile(7, TileColor.Black), new Tile(7, TileColor.Mango)]));

        var set = solution.ToSet();

        set.Tiles.Should().HaveCount(6);
    }

    [Fact]
    public void ToSet_IncludesJokersFromValidSets()
    {
        var solution = new Solution();
        solution.AddRun(new ValidSet([
            new Tile(1, TileColor.Blue),
            new Tile(isJoker: true),
            new Tile(3, TileColor.Blue)
        ]));

        var set = solution.ToSet();

        set.Jokers.Should().Be(1);
        set.Tiles.Should().HaveCount(2);
    }

    [Fact]
    public void ToSet_HandlesEmptySolution()
    {
        var solution = new Solution();

        var set = solution.ToSet();

        set.Tiles.Should().BeEmpty();
        set.Jokers.Should().Be(0);
    }

    [Fact]
    public void InvalidSolution_IsValidFalse()
    {
        Solution.InvalidSolution.IsValid.Should().BeFalse();
    }
}
