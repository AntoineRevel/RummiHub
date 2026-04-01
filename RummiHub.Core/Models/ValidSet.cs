namespace RummiHub.Core.Models;

/// <summary>
/// Represents a validated set (Run or Group) of tiles on the board.
/// Unlike <see cref="Set"/>, the Tiles array MAY contain jokers.
/// </summary>
public class ValidSet
{
    public Tile[] Tiles { get; init; } = [];

    public ValidSet() { }

    public ValidSet(Tile[] tiles) => Tiles = tiles;

    public int GetScore() => Tiles.Sum(t => t.Value);
}
