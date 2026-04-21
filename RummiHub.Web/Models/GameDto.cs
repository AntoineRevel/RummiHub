namespace RummiHub.Web.Models;

public class GameDto
{
    public string Id { get; set; } = "";
    public string? DistributionSeed { get; set; }
    public bool IsStarted { get; set; }
    public string HostConnectionId { get; set; } = "";
    public List<PlayerDto> Players { get; set; } = [];

    public static GameDto FromGame(Game game) => new()
    {
        Id = game.Id,
        DistributionSeed = game.DistributionSeed,
        IsStarted = game.IsStarted,
        HostConnectionId = game.HostConnectionId,
        Players = game.GetPlayers()
            .Select(p => new PlayerDto { Id = p.Id, Name = p.Name, ConnectionId = p.ConnectionId })
            .ToList()
    };
}

public class PlayerDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ConnectionId { get; set; } = "";
}
