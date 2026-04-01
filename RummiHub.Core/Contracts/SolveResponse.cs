using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

public record SolveResponse
{
    public required string RequestId { get; init; }
    public required bool Found { get; init; }

    /// <summary>
    /// New board state after the move (null if no valid move found).
    /// </summary>
    public Solution? NewBoard { get; init; }

    /// <summary>
    /// Tiles from the player's hand that were played.
    /// </summary>
    public List<Tile> TilesToPlay { get; init; } = [];

    public int JokersToPlay { get; init; }
    public int Score { get; init; }
    public string? Error { get; init; }
    public string? WorkerId { get; init; }
}
