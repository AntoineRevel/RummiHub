using AwesomeAssertions;
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
    public void Invalid_DefaultsScoreToZero()
    {
        SolverResult.Invalid("test").Score.Should().Be(0);
    }

    [Fact]
    public void Invalid_PropagatesScore()
    {
        // An invalid result may still carry the best score it could reach (e.g. a first play
        // that forms a legal set but falls short of 30), so the UI can show it instead of 0.
        var result = SolverResult.Invalid("test", score: 27);

        result.Found.Should().BeFalse();
        result.Score.Should().Be(27);
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
