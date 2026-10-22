using System.Text.Json.Serialization;

namespace RummiHub.Web.Models;

public class Player
{
    public required string Id { get; set; }
    public required string Name { get; init; }
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Unguessable secret for this seat. Sent only to the connection that created or joined
    /// it, and required to attach a later connection. Never broadcast and never serialized.
    /// </summary>
    [JsonIgnore]
    public required string SeatToken { get; init; }

    /// <summary>
    /// When true, this seat is an AI controlled by the solver (auto-play),
    /// rather than a human acting through a SignalR connection.
    /// </summary>
    public bool IsAuto { get; init; }
}