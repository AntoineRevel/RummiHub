using System.Text.Json;
using RummiHub.Core.Contracts;
using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Bank;

/// <summary>
/// File-backed corpus of benchmark positions: one JSON file per position (named by id),
/// serialized with the shared <c>RummiJsonOptions.Default</c>. Reloadable as a stable
/// benchmark / non-regression set, and enrichable by hand (from the UI) or automatically
/// (interesting positions detected during a trace or comparison).
/// </summary>
public sealed class MoveBank(string root)
{
    public string Root { get; } = root;

    public static MoveBank Default() => new(BankPaths.Default());

    /// <summary>Loads every well-formed entry, sorted by id for a stable order. Skips malformed files.</summary>
    public IReadOnlyList<BenchPosition> Load()
    {
        if (!Directory.Exists(Root)) return [];

        var entries = new List<BenchPosition>();
        foreach (var file in Directory.EnumerateFiles(Root, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            BenchPosition? entry = null;
            try
            {
                entry = JsonSerializer.Deserialize<BenchPosition>(File.ReadAllText(file), RummiJsonOptions.Default);
            }
            catch
            {
                // Ignore malformed files — the bank stays loadable even if one entry is broken.
            }

            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Writes (or overwrites) an entry; returns the file path. Same id ⇒ same file.</summary>
    public string Save(BenchPosition position)
    {
        Directory.CreateDirectory(Root);
        var path = PathFor(position.Id);
        File.WriteAllText(path, JsonSerializer.Serialize(position, RummiJsonOptions.Default));
        return path;
    }

    /// <summary>Adds an entry only if its id is not already present; returns true when written.</summary>
    public bool TryAdd(BenchPosition position)
    {
        if (Contains(position.Id)) return false;
        Save(position);
        return true;
    }

    public bool Contains(string id) => File.Exists(PathFor(id));

    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string id) => Path.Combine(Root, id + ".json");
}
