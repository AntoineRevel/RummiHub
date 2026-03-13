using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

public record SolveRequest
{
    public required string RequestId { get; init; }
    public required List<Tile> Hand { get; init; }
    public required Board Board { get; init; }
}
