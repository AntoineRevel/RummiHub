using System.Text.Json;
using System.Text.Json.Serialization;

namespace RummiHub.Core.Models;

/// <summary>
/// Represents a complete board state as one ordered list of valid sets, runs and groups
/// mixed. The order is the reading order on screen: a set the move rebuilt sits where its
/// tiles came from, whatever its kind.
/// Used both as solver output and as the canonical board representation.
/// </summary>
[JsonConverter(typeof(SolutionJsonConverter))]
public class Solution
{
    public static readonly Solution InvalidSolution = new() { IsValid = false };

    public List<ValidSet> Sets { get; set; } = [];
    public bool IsValid { get; set; }

    /// <summary>The runs of <see cref="Sets"/>, in board order. A view: adding to it changes nothing.</summary>
    public List<ValidSet> Runs => Sets.Where(s => !s.IsGroup).ToList();

    /// <summary>The groups of <see cref="Sets"/>, in board order. A view: adding to it changes nothing.</summary>
    public List<ValidSet> Groups => Sets.Where(s => s.IsGroup).ToList();

    public Solution AddSolution(Solution solution)
    {
        Sets.AddRange(solution.Sets);
        return this;
    }

    public void AddRun(ValidSet run) => Sets.Add(run);

    public void AddGroup(ValidSet group) => Sets.Add(group);

    /// <summary>
    /// Flattens this Solution into a <see cref="Set"/> (tiles + joker count).
    /// Used by solvers which take the board as a flat tile collection.
    /// </summary>
    public Set ToSet()
    {
        var result = new Set();

        foreach (var set in Sets) result.Concat(set);

        return result;
    }
}

/// <summary>
/// Writes <c>{sets, isValid}</c>. Also reads the former <c>{runs, groups, isValid}</c> shape
/// (move bank, Bruno requests, results cached or queued before the switch), runs first.
/// </summary>
public class SolutionJsonConverter : JsonConverter<Solution>
{
    public override Solution Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected a board object.");

        var solution = new Solution();
        List<ValidSet>? runs = null, groups = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            if (Is(name, "sets")) solution.Sets = ReadSets(ref reader, options);
            else if (Is(name, "runs")) runs = ReadSets(ref reader, options);
            else if (Is(name, "groups")) groups = ReadSets(ref reader, options);
            else if (Is(name, "isValid")) solution.IsValid = reader.GetBoolean();
            else reader.Skip();
        }

        if (runs is not null) solution.Sets.AddRange(runs);
        if (groups is not null) solution.Sets.AddRange(groups);
        return solution;
    }

    public override void Write(Utf8JsonWriter writer, Solution value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("sets");
        JsonSerializer.Serialize(writer, value.Sets, options);
        writer.WriteBoolean("isValid", value.IsValid);
        writer.WriteEndObject();
    }

    private static bool Is(string? name, string expected) =>
        string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);

    private static List<ValidSet> ReadSets(ref Utf8JsonReader reader, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null
            ? []
            : JsonSerializer.Deserialize<List<ValidSet>>(ref reader, options) ?? [];
}
