using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

public record SolveRequest
{
    public required string RequestId { get; init; }

    /// <summary>
    /// Player's rack tiles (including jokers as Tile with IsJoker=true).
    /// </summary>
    public required List<Tile> Hand { get; init; }

    /// <summary>
    /// Current board state as a structured solution (Runs + Groups).
    /// </summary>
    public required Solution Board { get; init; }

    /// <summary>
    /// Whether the player has already made their first play (30-point rule).
    /// </summary>
    public bool HasPlayed { get; init; }
}
