using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

/// <summary>
/// The seed a seated player may add to the shared list. It is the seed the server dealt,
/// for the whole game, never a string supplied by the caller. The value is not sent to the
/// browser until the game is over.
/// </summary>
public static class SeedPublication
{
    public static string? ForSeatedPlayer(Game? game, string? connectionId)
    {
        if (game is null || string.IsNullOrEmpty(connectionId) || !game.HasConnection(connectionId))
            return null;

        return string.IsNullOrWhiteSpace(game.DistributionSeed) ? null : game.DistributionSeed;
    }
}
