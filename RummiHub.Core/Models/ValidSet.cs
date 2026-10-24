using System.Text.Json.Serialization;

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

    /// <summary>
    /// True when every tile shows the same value (a group), false for a run. A joker counts
    /// with the value it stands for; one that stands for nothing (value 0) is ignored.
    /// </summary>
    [JsonIgnore]
    public bool IsGroup =>
        Tiles.Where(t => !t.IsJoker || t.Value > 0).Select(t => t.Value).Distinct().Count() <= 1;
}
