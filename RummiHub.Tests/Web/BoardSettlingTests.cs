using AwesomeAssertions;
using RummiHub.Web.Models;

namespace RummiHub.Tests.Web;

public class BoardSettlingTests
{
    // Moves "now" by hand; timers (the estimate's timeout) still run in real time.
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task NobodyWatching_IsIdleAtOnce()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        var shownAt = clock.Now;

        settling.Begin(60_000);

        (await settling.IdleAtAsync(CancellationToken.None).WaitAsync(Fast)).Should().Be(shownAt);
    }

    [Fact]
    public async Task EveryPageReported_IsIdleAtTheLatestReportedEnd_BeforeTheEstimate()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        settling.Watch("a");
        settling.Watch("b");
        var version = settling.Begin(60_000);
        var shownAt = clock.Now;

        var idle = settling.IdleAtAsync(CancellationToken.None);
        settling.Report("a", version, 3_000);
        idle.IsCompleted.Should().BeFalse();
        clock.Now += TimeSpan.FromMilliseconds(100);
        settling.Report("b", version, 1_000);

        (await idle.WaitAsync(Fast)).Should().Be(shownAt + TimeSpan.FromMilliseconds(3_000));
    }

    [Fact]
    public async Task ReportLongerThanTheEstimate_IsHonoured_UpToTheCap()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        settling.Watch("a");
        var version = settling.Begin(2_000);
        var shownAt = clock.Now;

        settling.Report("a", version, 999_999);

        (await settling.IdleAtAsync(CancellationToken.None).WaitAsync(Fast))
            .Should().Be(shownAt + TimeSpan.FromMilliseconds(BoardSettling.MaxReportedMs));
    }

    [Fact]
    public async Task SilentPage_FallsBackOnTheEstimate()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        settling.Watch("a");
        settling.Watch("silent");
        var version = settling.Begin(50);
        var shownAt = clock.Now;

        settling.Report("a", version, 10);

        (await settling.IdleAtAsync(CancellationToken.None).WaitAsync(Fast))
            .Should().Be(shownAt + TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task PageLeaving_StopsTheWaitForIt()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        settling.Watch("a");
        settling.Watch("gone");
        var version = settling.Begin(60_000);
        var shownAt = clock.Now;

        var idle = settling.IdleAtAsync(CancellationToken.None);
        settling.Report("a", version, 500);
        idle.IsCompleted.Should().BeFalse();
        settling.Forget("gone");

        (await idle.WaitAsync(Fast)).Should().Be(shownAt + TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task StaleBoardOrUnknownPage_IsIgnored()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        settling.Watch("a");
        var stale = settling.Begin(60_000);
        var version = settling.Begin(60_000);
        var shownAt = clock.Now;

        var idle = settling.IdleAtAsync(CancellationToken.None);
        settling.Report("a", stale, 9_000);
        settling.Report("intruder", version, 9_000);
        idle.IsCompleted.Should().BeFalse();
        settling.Report("a", version, 200);

        version.Should().Be(stale + 1);
        (await idle.WaitAsync(Fast)).Should().Be(shownAt + TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task NewBoard_ReleasesAWaitOnTheOldOne()
    {
        var clock = new Clock();
        var settling = new BoardSettling(clock);
        settling.Watch("a");
        settling.Begin(60_000);

        var idle = settling.IdleAtAsync(CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(1);
        var version = settling.Begin(60_000);
        settling.Report("a", version, 0);

        (await idle.WaitAsync(Fast)).Should().Be(clock.Now);
    }
}
