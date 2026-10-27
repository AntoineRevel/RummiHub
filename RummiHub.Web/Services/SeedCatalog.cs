using System.Text.Json;

namespace RummiHub.Web.Services;

/// <summary>One proposed distribution seed, with the moment it was added.</summary>
public sealed record CatalogSeed(string Seed, DateTimeOffset AddedAt);

public enum SeedAddStatus
{
    Added,
    AlreadyPresent,
    Invalid
}

public readonly record struct SeedAddResult(SeedAddStatus Status, string? Error)
{
    public static SeedAddResult Added() => new(SeedAddStatus.Added, null);
    public static SeedAddResult Already() => new(SeedAddStatus.AlreadyPresent, null);
    public static SeedAddResult Invalid(string error) => new(SeedAddStatus.Invalid, error);
}

/// <summary>
/// The shared list of proposed seeds. Newest first. There is no remove operation:
/// a caller can append one valid seed or read the list, and nothing else.
/// </summary>
public interface ISeedCatalog
{
    Task<IReadOnlyList<CatalogSeed>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> ContainsAsync(string seed, CancellationToken cancellationToken = default);

    Task<SeedAddResult> AddAsync(string seed, CancellationToken cancellationToken = default);
}

/// <summary>The catalog file or the prod endpoint cannot be used right now.</summary>
public sealed class SeedCatalogUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class SeedCatalogOptions
{
    public const string SectionName = "SeedCatalog";
    public const int DefaultMaxEntries = 500;
    public const string DefaultProductionPath = "/var/rummihub/seeds/catalog.jsonl";

    /// <summary><c>File</c> (this process owns the file) or <c>Remote</c> (call production).</summary>
    public string Mode { get; set; } = "File";

    public string Path { get; set; } = "";

    public string RemoteBaseAddress { get; set; } = "";

    public string ApiKey { get; set; } = "";

    public bool IsRemote =>
        string.Equals(Mode?.Trim(), "Remote", StringComparison.OrdinalIgnoreCase);

    public static string ResolvePath(SeedCatalogOptions options, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.Path))
            return options.Path;

        if (environment.IsDevelopment())
            return System.IO.Path.Combine(environment.ContentRootPath, "data", "seed-catalog.jsonl");

        return DefaultProductionPath;
    }
}

public static class SeedCatalogJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
