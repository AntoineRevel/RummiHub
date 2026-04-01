namespace RummiHub.Web.Models;

public class Player
{
    public required string Id { get; set; }
    public required string Name { get; init; }
    public string ConnectionId { get; set; } = string.Empty;
}