using System.Text.Json;
using FluentAssertions;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;

namespace RummiHub.Tests.Core;

public class SolutionTests
{
    [Fact]
    public void AddSolution_AppendsTheOtherSets()
    {
        var run = new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]);
        var group = new ValidSet([new Tile(5, TileColor.Blue), new Tile(5, TileColor.Red), new Tile(5, TileColor.Black)]);
        var a = new Solution { Sets = [run] };
        var b = new Solution { Sets = [group] };

        a.AddSolution(b);

        a.Sets.Should().Equal(run, group);
    }

    [Fact]
    public void ToSet_FlattensTiles()
    {
        var solution = new Solution();
        solution.Sets.Add(new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]));
        solution.Sets.Add(new ValidSet([new Tile(7, TileColor.Red), new Tile(7, TileColor.Black), new Tile(7, TileColor.Mango)]));

        var set = solution.ToSet();

        set.Tiles.Should().HaveCount(6);
    }

    [Fact]
    public void ToSet_IncludesJokersFromValidSets()
    {
        var solution = new Solution();
        solution.Sets.Add(new ValidSet([
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

    [Fact]
    public void Json_KeepsRunsAndGroupsInOneOrderedList()
    {
        var group = new ValidSet([new Tile(5, TileColor.Blue), new Tile(5, TileColor.Red), new Tile(5, TileColor.Black)]);
        var run = new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]);
        var board = new Solution { IsValid = true, Sets = [group, run] };

        var json = JsonSerializer.Serialize(board, RummiJsonOptions.Default);
        var back = JsonSerializer.Deserialize<Solution>(json, RummiJsonOptions.Default)!;

        json.Should().Contain("\"sets\"").And.NotContain("isGroup");
        back.IsValid.Should().BeTrue();
        back.Sets.Should().HaveCount(2);
        back.Sets[0].Tiles.Should().Equal(group.Tiles);
        back.Sets[1].Tiles.Should().Equal(run.Tiles);
    }

    [Fact]
    public void IsGroup_ReadsAJokerByTheValueItStandsFor()
    {
        new ValidSet([new Tile(4, TileColor.Red), new Tile(5, TileColor.Red, isJoker: true), new Tile(6, TileColor.Red)])
            .IsGroup.Should().BeFalse();
        new ValidSet([new Tile(9, TileColor.Red), new Tile(9, TileColor.Blue, isJoker: true), new Tile(9, TileColor.Black)])
            .IsGroup.Should().BeTrue();
    }
}
