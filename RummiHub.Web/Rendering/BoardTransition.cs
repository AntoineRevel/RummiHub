using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Counts how a board changed between two renders, using the same occurrence keys as
/// <see cref="BoardFlipKeys"/> so the server's animation-duration estimate matches the set of
/// tiles the client actually animates. Expects the <paramref name="next"/> board to already be
/// aligned (see <see cref="BoardAlignment"/>) so unchanged tiles keep their reading-order index.
/// </summary>
public static class BoardTransition
{
    /// <summary>
    /// <c>Moved</c> = tiles present in both boards whose global reading-order index changed.
    /// <c>Entered</c> = tiles present in <paramref name="next"/> but not in <paramref name="previous"/>.
    /// </summary>
    public static (int Moved, int Entered) Compute(Solution previous, Solution next)
    {
        var previousKeys = BoardFlipKeys.Assign(previous);
        var nextKeys = BoardFlipKeys.Assign(next);

        // Flip ids are unique within a board (occurrence-indexed), so a plain map is enough.
        var previousIndex = new Dictionary<string, int>(previousKeys.Count);
        for (var i = 0; i < previousKeys.Count; i++)
            previousIndex[previousKeys[i]] = i;

        var moved = 0;
        var entered = 0;
        for (var i = 0; i < nextKeys.Count; i++)
        {
            if (previousIndex.TryGetValue(nextKeys[i], out var oldIndex))
            {
                if (oldIndex != i) moved++;
            }
            else
            {
                entered++;
            }
        }

        return (moved, entered);
    }
}
