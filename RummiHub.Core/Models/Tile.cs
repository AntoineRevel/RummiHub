namespace RummiHub.Core.Models;

public record Tile(int Number, TileColor Color, bool IsJoker = false)
{
    public override string ToString() =>
        IsJoker ? "Joker" : $"{Color} {Number}";
}
