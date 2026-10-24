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
    /// Current board state as a structured solution (its ordered sets).
    /// </summary>
    public required Solution Board { get; init; }

    /// <summary>
    /// Whether the player has already made their first play (30-point rule).
    /// </summary>
    public bool HasPlayed { get; init; }

    /// <summary>
    /// When true, the request is routed to the priority stream so it jumps ahead of
    /// background optimal-play hints. Used for validating a human's actual move, which
    /// is small/fast and must not queue behind slow full-rack solves.
    /// Intentionally excluded from the content hash — priority does not change the result.
    /// </summary>
    public bool Priority { get; init; }
}
