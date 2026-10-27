using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RummiHub.Web.Services;

/// <summary>
/// Reads and appends through production's catalog endpoint. The test stack uses this so it
/// never mounts the catalog file. A failure leaves the file untouched.
/// </summary>
public sealed partial class RemoteSeedCatalog(IHttpClientFactory httpClientFactory, ILogger<RemoteSeedCatalog> logger)
    : ISeedCatalog
{
    public const string HttpClientName = "seed-catalog";

    public async Task<IReadOnlyList<CatalogSeed>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => Client.GetAsync(SeedCatalogApi.RelativePath, cancellationToken),
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw Unavailable(response.StatusCode);

        var seeds = await response.Content.ReadFromJsonAsync<List<CatalogSeed>>(SeedCatalogJson.Options, cancellationToken);
        return (seeds ?? [])
            .OrderByDescending(seed => seed.AddedAt)
            .ToList();
    }

    public async Task<bool> ContainsAsync(string seed, CancellationToken cancellationToken = default)
    {
        var parsed = DistributionSeeds.Parse(seed);
        if (parsed.Kind is not (SeedInputKind.Generated or SeedInputKind.Legacy))
            return false;

        var listed = await ListAsync(cancellationToken);
        return listed.Any(entry => entry.Seed == parsed.Normalized);
    }

    public async Task<SeedAddResult> AddAsync(string seed, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => Client.PostAsJsonAsync(
                SeedCatalogApi.RelativePath,
                new SeedCatalogWrite(seed),
                SeedCatalogJson.Options,
                cancellationToken),
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            var body = await ReadBodyAsync(response, cancellationToken);
            var status = body?.Status;
            if (string.Equals(status, "added", StringComparison.Ordinal))
                return SeedAddResult.Added();
            if (string.Equals(status, "already", StringComparison.Ordinal))
                return SeedAddResult.Already();
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.TooManyRequests)
        {
            var body = await ReadBodyAsync(response, cancellationToken);
            var error = body?.Error;
            if (string.IsNullOrWhiteSpace(error))
                error = "That seed could not be added to the list.";
            return SeedAddResult.Invalid(error);
        }

        throw Unavailable(response.StatusCode);
    }

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    private async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException)
                                    && !cancellationToken.IsCancellationRequested)
        {
            LogCatalogUnreachable(ex);
            throw new SeedCatalogUnavailableException("The shared seed list is unavailable.", ex);
        }
    }

    private static async Task<SeedCatalogResponse?> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<SeedCatalogResponse>(SeedCatalogJson.Options, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private SeedCatalogUnavailableException Unavailable(HttpStatusCode status)
    {
        LogCatalogRefused((int)status);
        return new SeedCatalogUnavailableException("The shared seed list is unavailable.");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Seed catalog endpoint is unreachable")]
    private partial void LogCatalogUnreachable(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Seed catalog endpoint refused the call ({Status})")]
    private partial void LogCatalogRefused(int status);

    private sealed record SeedCatalogResponse(string? Status, string? Error);
}
