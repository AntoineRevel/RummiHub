using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

public record SolveResponse
{
    public required string RequestId { get; init; }
    public required bool Solved { get; init; }
    public Board? NewBoard { get; init; }
    public List<Tile> RemainingHand { get; init; } = [];
    public string? Error { get; init; }
    public string? WorkerId { get; init; }
}
