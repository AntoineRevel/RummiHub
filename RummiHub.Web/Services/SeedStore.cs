using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

/// <summary>
/// A deal on the lobby list: the seed, how many players sat at the table, and the seat of
/// whoever saved it. A seed deals seat k the same rack whatever the table size, but the draw
/// pile starts after the last rack dealt: the same players and seat replay the same game.
/// </summary>
public sealed record SavedSeed(string Seed, int Players, int Seat, string? Name, DateTimeOffset AddedAt)
{
    /// <summary>Longest name kept; a longer one is cut.</summary>
    public const int MaxNameLength = 40;

    /// <summary>What a page names this entry by (<see cref="DistributionSeeds.ListId"/>).</summary>
    [JsonIgnore]
    public string ListId => DistributionSeeds.ListId(Seed, Players, Seat);

    /// <summary>A seed of the shape <see cref="DistributionSeeds.Create"/> makes, a table size the game allows, a seat at it.</summary>
    public static bool IsValid(string? seed, int players, int seat) =>
        DistributionSeeds.IsValid(seed)
        && players is >= GameLimits.MinPlayers and <= GameLimits.MaxPlayers
        && seat >= 0 && seat < players;

    /// <summary>The name as kept: one line, trimmed, at most <see cref="MaxNameLength"/> characters; null when empty.</summary>
    public static string? NormalizeName(string? name)
    {
        var oneLine = new string((name ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        var clean = string.Join(' ', oneLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > MaxNameLength)
            clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }
}

/// <summary>
/// The lobby seed list, persisted. Production and the test site share one file (a Docker
/// volume), so both offer the same seeds and keep them across deploys.
/// </summary>
public interface ISeedStore
{
    /// <summary>Every entry, most recently added first.</summary>
    IReadOnlyList<SavedSeed> GetAll();

    /// <summary>
    /// Puts the deal at the top of the list, dated now. The same seed, table size and seat
    /// already listed moves up, keeping its name when <paramref name="name"/> is empty.
    /// </summary>
    void Add(string seed, int players, int seat, string? name);

    /// <summary>Names the entry (<see cref="SavedSeed.ListId"/>); an empty name clears it. No-op if absent.</summary>
    void Rename(string listId, string? name);

    /// <summary>Takes the entry off the list. No-op if absent.</summary>
    void Remove(string listId);
}

/// <summary>
/// <see cref="ISeedStore"/> in one JSON file (<see cref="GameOptions.SeedsFile"/>). Several
/// processes write it (production and test site): every change holds an exclusive lock file,
/// re-reads, then replaces the file by a rename, so a reader never sees half a file. Failures
/// are logged, never thrown: the list must not break creating or playing a game.
/// </summary>
public sealed partial class FileSeedStore : ISeedStore
{
    /// <summary>Oldest seeds beyond this are dropped.</summary>
    public const int MaxSeeds = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<FileSeedStore> _logger;

    public FileSeedStore(string path, TimeProvider time, ILogger<FileSeedStore> logger)
    {
        _path = Path.GetFullPath(path);
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<SavedSeed> GetAll()
    {
        try
        {
            return Read();
        }
        catch (Exception ex)
        {
            LogReadFailed(ex, _path);
            return [];
        }
    }

    public void Add(string seed, int players, int seat, string? name)
    {
        if (!SavedSeed.IsValid(seed, players, seat))
            return;

        name = SavedSeed.NormalizeName(name);
        Change(seeds =>
        {
            var listId = DistributionSeeds.ListId(seed, players, seat);
            var previous = seeds.FirstOrDefault(s => s.ListId == listId);
            seeds.RemoveAll(s => s.ListId == listId);
            seeds.Insert(0, new SavedSeed(seed, players, seat, name ?? previous?.Name, _time.GetUtcNow()));
            if (seeds.Count > MaxSeeds)
                seeds.RemoveRange(MaxSeeds, seeds.Count - MaxSeeds);
            return true;
        });
        LogSeedSaved(seed, players, seat);
    }

    public void Rename(string listId, string? name)
    {
        name = SavedSeed.NormalizeName(name);
        Change(seeds =>
        {
            var index = seeds.FindIndex(s => s.ListId == listId);
            if (index < 0 || seeds[index].Name == name)
                return false;

            seeds[index] = seeds[index] with { Name = name };
            return true;
        });
    }

    public void Remove(string listId)
    {
        Change(seeds => seeds.RemoveAll(s => s.ListId == listId) > 0);
        LogSeedRemoved(listId);
    }

    private List<SavedSeed> Read()
    {
        if (!File.Exists(_path))
            return [];

        var file = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(_path), JsonOptions);
        // The test site runs any branch and writes here too: keep only well-formed entries.
        return (file?.Seeds ?? [])
            .Where(s => s is not null && SavedSeed.IsValid(s.Seed, s.Players, s.Seat))
            .Select(s => s with { Name = SavedSeed.NormalizeName(s.Name) })
            .DistinctBy(s => s.ListId)
            .OrderByDescending(s => s.AddedAt)
            .ToList();
    }

    private void Change(Func<List<SavedSeed>, bool> change)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var fileLock = AcquireFileLock();

                // A file that fails to parse throws here, before anything overwrites it.
                var seeds = Read();
                if (!change(seeds))
                    return;

                var temp = $"{_path}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new SeedFile(seeds), JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                LogWriteFailed(ex, _path);
            }
        }
    }

    /// <summary>Waits up to ~2 s for the other process to finish its change.</summary>
    private FileStream AcquireFileLock()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 40)
            {
                Thread.Sleep(50);
            }
        }
    }

    private sealed record SeedFile(List<SavedSeed> Seeds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed {Seed} added to the seed list ({Players} players, seat {Seat})")]
    private partial void LogSeedSaved(string seed, int players, int seat);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed list entry {ListId} removed")]
    private partial void LogSeedRemoved(string listId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the seed list {Path}")]
    private partial void LogReadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not update the seed list {Path}")]
    private partial void LogWriteFailed(Exception ex, string path);
}
