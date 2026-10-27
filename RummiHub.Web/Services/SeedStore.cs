using System.Text.Json;

namespace RummiHub.Web.Services;

/// <summary>A seed on the lobby list.</summary>
/// <param name="HiddenUntil">
/// Set while a game dealt from this seed may still be running: the seed rebuilds every rack,
/// so the lobby leaves it out until that game ends (or this time passes, if the server that
/// ran it restarted before the end).
/// </param>
public sealed record SavedSeed(string Seed, DateTimeOffset AddedAt, DateTimeOffset? HiddenUntil = null)
{
    public bool IsVisible(DateTimeOffset now) => HiddenUntil is null || HiddenUntil <= now;
}

/// <summary>
/// The lobby seed list, persisted. Production and the test site share one file (a Docker
/// volume), so both offer the same seeds and keep them across deploys.
/// </summary>
public interface ISeedStore
{
    /// <summary>Every seed, hidden ones included, most recently added first.</summary>
    IReadOnlyList<SavedSeed> GetAll();

    /// <summary>
    /// Puts the seed at the top of the list, dated now. A seed already listed moves up; it
    /// stays visible if it was (it is public already) and is hidden only when both say so.
    /// </summary>
    void Add(string seed, DateTimeOffset? hiddenUntil = null);

    /// <summary>Shows a seed hidden by <see cref="Add"/>. No-op if absent or visible.</summary>
    void Reveal(string seed);
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

    public void Add(string seed, DateTimeOffset? hiddenUntil = null)
    {
        if (!DistributionSeeds.IsValid(seed))
            return;

        Change(seeds =>
        {
            var existing = seeds.FindIndex(s => s.Seed == seed);
            if (existing >= 0)
            {
                var old = seeds[existing];
                seeds.RemoveAt(existing);
                if (old.HiddenUntil is null || hiddenUntil is null)
                    hiddenUntil = null;
                else if (old.HiddenUntil > hiddenUntil)
                    hiddenUntil = old.HiddenUntil;
            }

            seeds.Insert(0, new SavedSeed(seed, _time.GetUtcNow(), hiddenUntil));
            if (seeds.Count > MaxSeeds)
                seeds.RemoveRange(MaxSeeds, seeds.Count - MaxSeeds);
            return true;
        });
        LogSeedSaved(seed, hiddenUntil is not null);
    }

    public void Reveal(string seed) =>
        Change(seeds =>
        {
            var i = seeds.FindIndex(s => s.Seed == seed);
            if (i < 0 || seeds[i].HiddenUntil is null)
                return false;

            seeds[i] = seeds[i] with { HiddenUntil = null };
            return true;
        });

    private List<SavedSeed> Read()
    {
        if (!File.Exists(_path))
            return [];

        var file = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(_path), JsonOptions);
        // The test site runs any branch and writes here too: keep only well-formed entries.
        return (file?.Seeds ?? [])
            .Where(s => s is not null && DistributionSeeds.IsValid(s.Seed))
            .DistinctBy(s => s.Seed)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed {Seed} added to the seed list (hidden while its game runs: {Hidden})")]
    private partial void LogSeedSaved(string seed, bool hidden);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read the seed list {Path}")]
    private partial void LogReadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not update the seed list {Path}")]
    private partial void LogWriteFailed(Exception ex, string path);
}
