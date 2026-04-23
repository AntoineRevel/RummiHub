using RummiHub.Core.Models;

namespace RummiHub.Web.Models;

/// <summary>
/// Game state broadcast to all players in the group (no private rack data).
/// </summary>
public record GameStateDto
{
    public required string GameId { get; init; }
    public string? DistributionSeed { get; init; }
    public required List<PlayerInfoDto> Players { get; init; }
    public required int CurrentPlayerIndex { get; init; }
    public required int Turn { get; init; }
    public required Solution Board { get; init; }
    public required bool IsGameOver { get; init; }
    public string? WinnerName { get; init; }
    public int TilePoolCount { get; init; }
}

/// <summary>
/// Per-player info visible to all (no rack tiles).
/// </summary>
public record PlayerInfoDto
{
    public required string Name { get; init; }
    public required int TileCount { get; init; }
    public required bool HasPlayed { get; init; }
}
