namespace RummiHub.Web.Services;

/// <summary>
/// The <c>/admin</c> pages (games in progress). Caddy asks for the password (<c>basic_auth</c>,
/// deploy/Caddyfile) and passes the <c>Authorization</c> header on once it has checked it. Outside
/// Development the app also answers 404 to a request without that header: a deployment whose
/// Caddy does not guard the path yet does not show the page to everyone.
/// </summary>
public static class AdminAccess
{
    public const string PathPrefix = "/admin";

    public static bool Allows(bool isDevelopment, string? authorizationHeader) =>
        isDevelopment || authorizationHeader?.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) == true;
}
