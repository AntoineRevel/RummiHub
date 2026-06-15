namespace RummiHub.Web.Models;

public class Player
{
    public required string Id { get; set; }
    public required string Name { get; init; }
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// When true, this seat is an AI controlled by the solver (auto-play),
    /// rather than a human acting through a SignalR connection.
    /// </summary>
    public bool IsAuto { get; init; }
}