using System.Text.Json;

namespace RummiHub.Web.Services;

/// <summary>
/// Append-only JSONL catalog. Each add writes one line and never rewrites the file, so a crash
/// cannot drop seeds already stored. Lines that do not parse are skipped. There is no delete.
/// </summary>
public sealed partial class FileSeedCatalog : ISeedCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IReadOnlyList<string> _bootstrap;
    private readonly int _maxEntries;
    private List<StoredSeed>? _loaded;

    public FileSeedCatalog(
        string path,
        IEnumerable<string>? bootstrap,
        ILogger<FileSeedCatalog> logger,
        int maxEntries = SeedCatalogOptions.DefaultMaxEntries)
    {
        _path = path;
        _bootstrap = bootstrap?.ToList() ?? [];
        _maxEntries = maxEntries;
        Logger = logger;
    }

    private ILogger<FileSeedCatalog> Logger { get; }

    public Task<IReadOnlyList<CatalogSeed>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(Sorted(Load()));
    }

    public Task<bool> ContainsAsync(string seed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = DistributionSeeds.Parse(seed);
        if (parsed.Kind is not (SeedInputKind.Generated or SeedInputKind.Legacy))
            return Task.FromResult(false);

        lock (_gate)
            return Task.FromResult(Load().Any(entry => entry.Seed == parsed.Normalized));
    }

    public Task<SeedAddResult> AddAsync(string seed, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = DistributionSeeds.Parse(seed);
        lock (_gate)
        {
            var entries = Load();
            if (parsed.Kind is SeedInputKind.Generated or SeedInputKind.Legacy
                && entries.Any(entry => entry.Seed == parsed.Normalized))
                return Task.FromResult(SeedAddResult.Already());

            if (parsed.Kind != SeedInputKind.Generated)
            {
                var error = parsed.Kind == SeedInputKind.Legacy
                    ? DistributionSeeds.LegacyNotListedMessage(parsed.Normalized)
                    : parsed.Error;
                return Task.FromResult(SeedAddResult.Invalid(error));
            }

            if (entries.Count >= _maxEntries)
                return Task.FromResult(SeedAddResult.Invalid(
                    $"The proposed seed list is full ({_maxEntries} seeds). A seed already on the list can still be used."));

            var stored = new StoredSeed(parsed.Normalized, DateTimeOffset.UtcNow, NextOrder(entries));
            try
            {
                Append(stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogCatalogUnreadable(ex, _path);
                throw new SeedCatalogUnavailableException("The shared seed list is unavailable.", ex);
            }

            entries.Add(stored);
            LogSeedAppended(parsed.Normalized, _path);
            return Task.FromResult(SeedAddResult.Added());
        }
    }

    private List<StoredSeed> Load()
    {
        if (_loaded is not null)
            return _loaded;

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(_path))
                WriteBootstrap();

            _loaded = ReadAll();
            return _loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCatalogUnreadable(ex, _path);
            throw new SeedCatalogUnavailableException("The shared seed list is unavailable.", ex);
        }
    }

    private void WriteBootstrap()
    {
        var epoch = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;
        foreach (var raw in _bootstrap)
        {
            var parsed = DistributionSeeds.Parse(raw);
            if (parsed.Kind is not (SeedInputKind.Generated or SeedInputKind.Legacy))
                continue;
            if (!seen.Add(parsed.Normalized))
                continue;

            var stored = new StoredSeed(parsed.Normalized, epoch.AddSeconds(order), order);
            lines.Add(JsonSerializer.Serialize(stored.ToCatalog(), SeedCatalogJson.Options));
            order++;
        }

        var body = lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
        File.WriteAllText(_path, body);
    }

    private List<StoredSeed> ReadAll()
    {
        var entries = new List<StoredSeed>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var order = 0;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            CatalogSeed? entry;
            try
            {
                entry = JsonSerializer.Deserialize<CatalogSeed>(line, SeedCatalogJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null || string.IsNullOrWhiteSpace(entry.Seed))
                continue;

            var parsed = DistributionSeeds.Parse(entry.Seed);
            if (parsed.Kind is not (SeedInputKind.Generated or SeedInputKind.Legacy))
                continue;
            if (!seen.Add(parsed.Normalized))
                continue;

            entries.Add(new StoredSeed(parsed.Normalized, entry.AddedAt, order));
            order++;
        }

        return entries;
    }

    private void Append(StoredSeed stored)
    {
        var line = JsonSerializer.Serialize(stored.ToCatalog(), SeedCatalogJson.Options) + "\n";
        using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static int NextOrder(List<StoredSeed> entries) =>
        entries.Count == 0 ? 0 : entries.Max(entry => entry.Order) + 1;

    private static IReadOnlyList<CatalogSeed> Sorted(List<StoredSeed> entries) =>
        entries
            .OrderByDescending(entry => entry.AddedAt)
            .ThenByDescending(entry => entry.Order)
            .Select(entry => entry.ToCatalog())
            .ToList();

    private sealed record StoredSeed(string Seed, DateTimeOffset AddedAt, int Order)
    {
        public CatalogSeed ToCatalog() => new(Seed, AddedAt);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed {Seed} appended to {Path}")]
    private partial void LogSeedAppended(string seed, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Seed catalog at {Path} is not readable")]
    private partial void LogCatalogUnreadable(Exception ex, string path);
}
