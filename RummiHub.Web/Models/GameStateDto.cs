using RummiHub.Core.Models;

namespace RummiHub.Web.Models;

/// <summary>
/// Game state broadcast to all players in the group (no private rack data).
/// </summary>
public record GameStateDto
{
    public required string GameId { get; init; }
    /// <summary>The full seed, at game over only: during play it would rebuild every rack.</summary>
    public string? DistributionSeed { get; init; }

    /// <summary>The seed shortened (<see cref="Services.DistributionSeeds.Abbreviate"/>), from the start.</summary>
    public string? SeedShort { get; init; }

    /// <summary>The game's seed is on the lobby list.</summary>
    public bool SeedSaved { get; init; }
    public required List<PlayerInfoDto> Players { get; init; }
    public required int CurrentPlayerIndex { get; init; }
    public required int Turn { get; init; }
    public required Solution Board { get; init; }
    public required bool IsGameOver { get; init; }
    public string? WinnerName { get; init; }
    public int TilePoolCount { get; init; }

    /// <summary>
    /// True while the current player has played in "review" mode and must confirm
    /// (Next player) before the turn advances.
    /// </summary>
    public bool AwaitingConfirmation { get; init; }
}

/// <summary>
/// Per-player info visible to all (no rack tiles).
/// </summary>
public record PlayerInfoDto
{
    public required string Name { get; init; }
    public required int TileCount { get; init; }
    public required bool HasPlayed { get; init; }
    public bool IsAuto { get; init; }
}

/// <summary>
/// A seed offered in the lobby: its list id and short form, never the seed itself (the hub is
/// public), with the time it was added to the list.
/// </summary>
public record SeedListItemDto(string Id, string Short, DateTimeOffset AddedAt);
