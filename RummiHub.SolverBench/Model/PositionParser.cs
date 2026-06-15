using RummiHub.Core.Models;
using RummiHub.SolverBench.Model;
using RummiHub.Solver.Worker.Solver.Dynamic;

namespace RummiHub.SolverBench;

/// <summary>
/// Parses a compact, human-typeable tile notation so positions can be entered or pasted from
/// the UI. A tile is <c>value + colour</c> (<c>B</c>lue, <c>R</c>ed, <c>M</c>ango, blac<c>K</c>),
/// e.g. <c>10B</c>, <c>7r</c>, <c>13m</c>; a joker is <c>J</c> (or <c>*</c>). Tiles are separated
/// by commas or whitespace, e.g. <c>"10B, 11B, 12B, J"</c>.
/// </summary>
public static class PositionParser
{
    public static List<Tile> ParseHand(string text)
    {
        var tiles = new List<Tile>();
        if (string.IsNullOrWhiteSpace(text)) return tiles;

        foreach (var token in text.Split([',', ' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
            tiles.Add(ParseTile(token.Trim()));

        return tiles;
    }

    public static Tile ParseTile(string token)
    {
        if (token is "J" or "j" or "*") return new Tile(isJoker: true);

        var colorChar = char.ToUpperInvariant(token[^1]);
        if (!int.TryParse(token[..^1], out var value) || value is < 1 or > 13)
            throw new FormatException($"Invalid tile '{token}'. Use e.g. 10B, 7R, 13M, 5K or J.");

        var color = colorChar switch
        {
            'B' => TileColor.Blue,
            'R' => TileColor.Red,
            'M' => TileColor.Mango,
            'K' => TileColor.Black,
            _ => throw new FormatException($"Invalid colour in '{token}'. Use B, R, M or K.")
        };

        return new Tile(value, color);
    }

    /// <summary>
    /// Builds a first-play position from a hand string, computing the DynamicSolver reference.
    /// Manual bank entry only supports opening positions; richer in-game positions are curated
    /// from a generated trace.
    /// </summary>
    public static BenchPosition FirstPlayFromHand(string handText, string name = "", IEnumerable<string>? tags = null)
    {
        var hand = ParseHand(handText);
        if (hand.Count == 0) throw new FormatException("The hand is empty.");

        var board = new Solution();
        var reference = ReferenceBest.From(DynamicSolver.CreateFirst(BuildSet(hand)).SearchSolution());

        var tagList = new List<string> { "first-play" };
        if (tags is not null) tagList.AddRange(tags);

        return new BenchPosition
        {
            Id = BenchPosition.ComputeId(hand, board, hasPlayed: false),
            Name = string.IsNullOrWhiteSpace(name) ? "manual first-play" : name,
            Tags = tagList.Distinct().ToList(),
            HasPlayed = false,
            Board = board,
            Hand = hand,
            Provenance = "manual",
            ReferenceBest = reference,
            Notes = "manually entered"
        };
    }

    private static Set BuildSet(List<Tile> tiles)
    {
        var set = new Set();
        foreach (var tile in tiles) set.AddTile(tile);
        return set;
    }
}
