using System.Text.Json;
using System.Text.Json.Serialization;

namespace RummiHub.Core.Models;

/// <summary>
/// Represents a Rummikub tile using bit-packed byte storage for performance.
/// Layout: [Bit6: IsJoker] [Bits 4-5: Color] [Bits 0-3: Value]
/// </summary>
[JsonConverter(typeof(TileJsonConverter))]
public readonly struct Tile : IComparable<Tile>, IEquatable<Tile>
{
    private readonly byte _data;

    public Tile(int value, TileColor color = TileColor.Blue, bool isJoker = false)
    {
        if (value is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be between 0 and 15.");

        if (!Enum.IsDefined(color))
            throw new ArgumentOutOfRangeException(nameof(color), "Invalid color.");

        _data = (byte)(
            ((isJoker ? 1 : 0) << 6) |
            ((int)color << 4) |
            (value & 0x0F)
        );
    }

    public Tile(bool isJoker = false)
    {
        if (isJoker) _data = (byte)(1 << 6);
    }

    public Tile(byte b)
    {
        _data = b;
    }

    public int Value => _data & 0x0F;

    public TileColor Color => (TileColor)((_data >> 4) & 0x03);

    public bool IsJoker => ((_data >> 6) & 0x01) == 1;

    public bool IsNull => _data == 0;

    public int CompareTo(Tile other) => _data.CompareTo(other._data);

    public bool Equals(Tile other)
    {
        if (IsJoker && other.IsJoker) return true;
        return _data == other._data;
    }

    public override bool Equals(object? obj) => obj is Tile other && Equals(other);

    public override int GetHashCode() => IsJoker ? 0xFF : _data.GetHashCode();

    public static bool operator ==(Tile left, Tile right) => left.Equals(right);

    public static bool operator !=(Tile left, Tile right) => !left.Equals(right);

    public override string ToString() => IsJoker ? "Joker" : $"{Color} {Value}";
}

/// <summary>
/// JSON converter for the bit-packed Tile struct.
/// Serializes as {"value":5,"color":"Red","isJoker":false}.
/// </summary>
public sealed class TileJsonConverter : JsonConverter<Tile>
{
    private record TileDto(int Value, TileColor Color, bool IsJoker);

    public override Tile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize<TileDto>(ref reader, options);
        if (dto is null) return default;
        return dto.IsJoker ? new Tile(isJoker: true) : new Tile(dto.Value, dto.Color);
    }

    public override void Write(Utf8JsonWriter writer, Tile value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, new TileDto(value.Value, value.Color, value.IsJoker), options);
    }
}
