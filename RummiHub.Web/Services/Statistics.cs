using Microsoft.JSInterop;

namespace RummiHub.Web.Services;

/// <summary>
/// Umami site ids by host name (<c>Umami:Websites</c> in appsettings.json). Caddy serves the
/// tracker under <c>/stats/</c> on each site (deploy/README.md, « Statistiques »). A host
/// with no entry (local runs) loads no tracker. The ids are public: they are in every page.
/// </summary>
public sealed class UmamiOptions
{
    public const string SectionName = "Umami";

    public Dictionary<string, string> Websites { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Umami's own interface, linked from the admin page.</summary>
    public string? DashboardUrl { get; set; }

    public string? WebsiteFor(string? host) =>
        host is not null && Websites.TryGetValue(host, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
}

/// <summary>Game events sent to Umami, named for the dashboard.</summary>
public static class StatsEvents
{
    public const string GameCreated = "partie-creee";
    public const string GameJoined = "partie-rejointe";
    public const string GameStarted = "partie-lancee";
    public const string GameOver = "partie-terminee";

    /// <summary>
    /// Sends an event through the page's tracker (wwwroot/js/stats.js). Best-effort: without
    /// the tracker (local run, blocked by the browser) or once the circuit is gone, nothing happens.
    /// </summary>
    public static async Task TrackAsync(this IJSRuntime js, string name, object? data = null)
    {
        try
        {
            await js.InvokeVoidAsync("rummiStats.track", name, data);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException
                                       or InvalidOperationException)
        {
            // Statistics never get in the way of the game.
        }
    }
}
