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
}
