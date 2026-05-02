using FluentAssertions;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.Tests.Core;

public class SolverResultTests
{
    [Fact]
    public void Invalid_HasFoundFalse()
    {
        var result = SolverResult.Invalid("test");

        result.Found.Should().BeFalse();
    }

    [Fact]
    public void Invalid_HasCorrectSource()
    {
        var result = SolverResult.Invalid("MySolver");

        result.Source.Should().Be("MySolver");
    }

    [Fact]
    public void FromSolution_InvalidSolution_ReturnsInvalid()
    {
        var solution = new Solution { IsValid = false };

        var result = SolverResult.FromSolution("src", solution, []);

        result.Found.Should().BeFalse();
    }

    [Fact]
    public void FromSolution_ValidSolution_ReturnsFound()
    {
        var solution = new Solution { IsValid = true };

        var result = SolverResult.FromSolution("src", solution, []);

        result.Found.Should().BeTrue();
        result.Source.Should().Be("src");
    }

    [Fact]
    public void FromSolution_PropagatesScore()
    {
        var solution = new Solution { IsValid = true };

        var result = SolverResult.FromSolution("src", solution, [], score: 42);

        result.Score.Should().Be(42);
    }

    [Fact]
    public void FromSolution_PropagatesTilesToPlay()
    {
        var solution = new Solution { IsValid = true };
        var tiles = new List<Tile> { new(3, TileColor.Blue), new(4, TileColor.Red) };

        var result = SolverResult.FromSolution("src", solution, tiles);

        result.TilesToPlay.Should().BeEquivalentTo(tiles);
    }
}
