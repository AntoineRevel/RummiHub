namespace RummiHub.Web.Rendering;

/// <summary>
/// Timing for the sequential board tile animation, and the single source of truth for it.
/// The same values are pushed to the JS module (<c>wwwroot/js/tile-animations.js</c>) via its
/// <c>configure()</c> export, and used server-side to estimate how long a transition's
/// animation runs so AI pacing can wait for it to finish before the next move.
/// Tiles animate one at a time, so per-step durations add up.
/// </summary>
public static class BoardAnimation
{
    /// <summary>Duration of one tile's glide from its old to its new position.</summary>
    public const int MoveStepMs = 350;

    /// <summary>Duration of one entering tile's fade + zoom in.</summary>
    public const int EnterStepMs = 300;

    /// <summary>
    /// Slack added on top of the steps so the server delay is a safe upper bound on the
    /// client animation (which may animate a couple of extra incidental-shift tiles).
    /// </summary>
    public const int BufferMs = 250;

    /// <summary>
    /// Total animation duration (ms) for a transition that moves <paramref name="moved"/> tiles
    /// and brings in <paramref name="entered"/> new ones. Zero when nothing changes (e.g. a draw).
    /// </summary>
    public static int Duration(int moved, int entered)
    {
        if (moved <= 0 && entered <= 0) return 0;
        return moved * MoveStepMs + entered * EnterStepMs + BufferMs;
    }
}
