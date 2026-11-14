using RummiHub.Core.Models;

namespace RummiHub.Web.Rendering;

/// <summary>
/// Cosmetically reorders a freshly-solved board so its sets line up with the board previously
/// shown to the player. The solver rebuilds the board from scratch (and sorts runs by length),
/// so unchanged sets would otherwise jump to new screen positions and animate for no reason.
/// Aligning (<see cref="BoardSeating.Seat"/>) keeps unchanged sets in place and lays new sets
/// after them, so only genuinely relocated or new tiles move and the rest of the board is
/// not pushed around. Only the ORDER of the sets changes; the partition (which tiles
/// form which set) is untouched, so this has no game effect.
/// </summary>
public static class BoardAlignment
{
    public static Solution Align(Solution previous, Solution next) => new()
    {
        Sets = BoardSeating.Seat(previous.Sets, next.Sets),
        IsValid = next.IsValid
    };
}
