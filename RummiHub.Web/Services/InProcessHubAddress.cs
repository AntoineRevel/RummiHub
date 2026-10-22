using System.Text.RegularExpressions;

namespace RummiHub.Web.Services;

/// <summary>
/// Base address for the SignalR client the Blazor circuit opens back into this process.
/// In a container Kestrel reports <c>http://[::]:8080</c>, which is not a legal client
/// target. Wildcards are rewritten to loopback; Aspire's localhost addresses are kept.
/// </summary>
public static partial class InProcessHubAddress
{
    public static string? TryGetBaseAddress(IEnumerable<string>? listenAddresses)
    {
        if (listenAddresses is null)
            return null;

        string? https = null;
        foreach (var raw in listenAddresses)
        {
            if (!TryNormalize(raw, out var normalized, out var scheme))
                continue;

            if (scheme == "http")
                return normalized;

            https ??= normalized;
        }

        return https;
    }

    private static bool TryNormalize(string raw, out string normalized, out string scheme)
    {
        normalized = string.Empty;
        scheme = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            && uri.Port > 0)
        {
            scheme = uri.Scheme;
            normalized = IsWildcard(uri.Host)
                ? $"{scheme}://127.0.0.1:{uri.Port}"
                : uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        // http://+:8080 and http://*:8080 are not valid URIs, but Kestrel can report them.
        var match = WildcardListen().Match(trimmed);
        if (!match.Success)
            return false;

        scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        normalized = $"{scheme}://127.0.0.1:{match.Groups["port"].Value}";
        return true;
    }

    private static bool IsWildcard(string host) =>
        host is "0.0.0.0" or "::" or "[::]" or "+" or "*";

    [GeneratedRegex(@"^(?<scheme>https?)://(?:\+|\*|0\.0\.0\.0|\[::\]):(?<port>\d+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WildcardListen();
}
