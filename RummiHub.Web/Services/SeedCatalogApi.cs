using System.Security.Cryptography;
using System.Text;

namespace RummiHub.Web.Services;

/// <summary>Body of <c>POST /internal/seed-catalog</c>.</summary>
public sealed record SeedCatalogWrite(string? Seed);

/// <summary>
/// List and append only. Mounted on the process that owns the file, and only when a long
/// shared key is configured. The public reverse proxy does not forward this path.
/// </summary>
public static class SeedCatalogApi
{
    public const string Path = "/internal/seed-catalog";
    public const string RelativePath = "internal/seed-catalog";
    public const string KeyHeader = "X-Seed-Catalog-Key";
    public const int MinimumKeyLength = 32;
    public const int MaxAddsPerMinute = 60;

    public static bool IsKeyStrong(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Trim().Length >= MinimumKeyLength;

    public static void Map(WebApplication app, string apiKey)
    {
        var expected = apiKey.Trim();
        app.MapGet(Path, async (HttpRequest request, ISeedCatalog catalog, CancellationToken cancellationToken) =>
        {
            if (!Authorize(request, expected))
                return Results.Unauthorized();

            var seeds = await catalog.ListAsync(cancellationToken);
            return Results.Json(seeds, SeedCatalogJson.Options);
        });

        app.MapPost(Path, async (
                HttpRequest request,
                SeedCatalogWrite? body,
                ISeedCatalog catalog,
                SeedCatalogRateGate rateGate,
                CancellationToken cancellationToken) =>
            {
                if (!Authorize(request, expected))
                    return Results.Unauthorized();

                if (!rateGate.TryEnter(DateTimeOffset.UtcNow))
                    return Results.Json(
                        new { status = "invalid", error = "Too many seeds were added just now. Wait a moment and try again." },
                        SeedCatalogJson.Options,
                        statusCode: StatusCodes.Status429TooManyRequests);

                var result = await catalog.AddAsync(body?.Seed ?? "", cancellationToken);
                return ToResult(result);
            })
            .DisableAntiforgery();
    }

    public static bool Authorize(HttpRequest request, string expectedKey)
    {
        if (string.IsNullOrEmpty(expectedKey))
            return false;
        if (!request.Headers.TryGetValue(KeyHeader, out var provided))
            return false;

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided.ToString()));
        return CryptographicOperations.FixedTimeEquals(expectedHash, providedHash);
    }

    public static IResult ToResult(SeedAddResult result) => result.Status switch
    {
        SeedAddStatus.Added => Results.Json(
            new { status = "added" }, SeedCatalogJson.Options, statusCode: StatusCodes.Status201Created),
        SeedAddStatus.AlreadyPresent => Results.Json(
            new { status = "already" }, SeedCatalogJson.Options),
        _ => Results.Json(
            new { status = "invalid", error = result.Error ?? "That seed could not be added to the list." },
            SeedCatalogJson.Options,
            statusCode: StatusCodes.Status400BadRequest)
    };
}

/// <summary>Caps how fast the catalog endpoint accepts new seeds.</summary>
public sealed class SeedCatalogRateGate
{
    private readonly Queue<DateTimeOffset> _hits = new();
    private readonly Lock _gate = new();

    public bool TryEnter(DateTimeOffset now, int maxPerMinute = SeedCatalogApi.MaxAddsPerMinute)
    {
        lock (_gate)
        {
            var cutoff = now.AddMinutes(-1);
            while (_hits.Count > 0 && _hits.Peek() <= cutoff)
                _hits.Dequeue();

            if (_hits.Count >= maxPerMinute)
                return false;

            _hits.Enqueue(now);
            return true;
        }
    }
}
