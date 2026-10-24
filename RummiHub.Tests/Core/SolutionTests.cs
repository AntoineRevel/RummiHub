using System.Text.Json;
using FluentAssertions;
using RummiHub.Core.Contracts;
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

    [Fact]
    public void Json_KeepsRunsAndGroupsInOneOrderedList()
    {
        var group = new ValidSet([new Tile(5, TileColor.Blue), new Tile(5, TileColor.Red), new Tile(5, TileColor.Black)]);
        var run = new ValidSet([new Tile(1, TileColor.Blue), new Tile(2, TileColor.Blue), new Tile(3, TileColor.Blue)]);
        var board = new Solution { IsValid = true, Sets = [group, run] };

        var json = JsonSerializer.Serialize(board, RummiJsonOptions.Default);
        var back = JsonSerializer.Deserialize<Solution>(json, RummiJsonOptions.Default)!;

        json.Should().Contain("\"sets\"").And.NotContain("runs");
        back.IsValid.Should().BeTrue();
        back.Sets.Select(s => s.IsGroup).Should().Equal(true, false);
        back.Groups.Should().ContainSingle().Which.Tiles.Should().Equal(group.Tiles);
    }

    [Fact]
    public void Json_StillReadsTheFormerRunsAndGroupsShape()
    {
        const string json = """
            {"runs":[{"tiles":[{"value":1,"color":"Blue","isJoker":false},{"value":2,"color":"Blue","isJoker":false},{"value":3,"color":"Blue","isJoker":false}]}],
             "groups":[{"tiles":[{"value":7,"color":"Red","isJoker":false},{"value":7,"color":"Black","isJoker":false},{"value":7,"color":"Mango","isJoker":false}]}],
             "isValid":true}
            """;

        var board = JsonSerializer.Deserialize<Solution>(json, RummiJsonOptions.Default)!;

        board.IsValid.Should().BeTrue();
        board.Sets.Should().HaveCount(2);
        board.Runs.Should().ContainSingle().Which.Tiles[0].Value.Should().Be(1);
        board.Groups.Should().ContainSingle().Which.Tiles[0].Value.Should().Be(7);
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
