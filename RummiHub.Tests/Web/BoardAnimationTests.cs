using FluentAssertions;
using RummiHub.Web.Rendering;

namespace RummiHub.Tests.Web;

public class BoardAnimationTests
{
    [Fact]
    public void Duration_NothingChanged_IsZero()
    {
        BoardAnimation.Duration(0, 0).Should().Be(0);
    }

    [Fact]
    public void Duration_MovesAndEnters_SumStepsPlusBuffer()
    {
        var expected = 2 * BoardAnimation.MoveStepMs + 1 * BoardAnimation.EnterStepMs + BoardAnimation.BufferMs;
        BoardAnimation.Duration(2, 1).Should().Be(expected);
    }

    [Fact]
    public void Duration_SingleMove_IncludesBuffer()
    {
        BoardAnimation.Duration(1, 0).Should().Be(BoardAnimation.MoveStepMs + BoardAnimation.BufferMs);
    }
}
