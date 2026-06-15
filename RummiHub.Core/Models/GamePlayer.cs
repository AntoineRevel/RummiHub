namespace RummiHub.Core.Models;

/// <summary>
/// Lightweight game-state player model for UI rendering.
/// Distinct from RummiHub.Web.Models.Player which is the SignalR lobby model.
/// </summary>
public class GamePlayer
{
    public required string Name { get; init; }
    public Set Rack { get; set; } = new();
    public bool HasPlayed { get; set; }
    public List<Tile> TilesToPlay { get; set; } = [];

    /// <summary>
    /// When true, this player is driven by the solver: the server plays the optimal
    /// move automatically on their turn instead of waiting for a human action.
    /// </summary>
    public bool IsAuto { get; set; }
}
