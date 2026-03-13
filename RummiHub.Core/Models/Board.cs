namespace RummiHub.Core.Models;

public class Board
{
    public List<TileGroup> Groups { get; set; } = [];

    public bool IsValid => Groups.All(g => g.IsValid);

    public List<Tile> AllTiles => Groups.SelectMany(g => g.Tiles).ToList();
}
