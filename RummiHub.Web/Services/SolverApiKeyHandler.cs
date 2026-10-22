using Microsoft.Extensions.Options;
using RummiHub.Core.Contracts;

namespace RummiHub.Web.Services;

/// <summary>
/// Attaches the shared solver secret to outbound calls from the web frontend.
/// No header is sent when the key is unset (Development only; the API refuses it otherwise).
/// </summary>
public sealed class SolverApiKeyHandler(IOptions<SolverApiOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = options.Value.Key?.Trim();
        if (!string.IsNullOrEmpty(key))
            request.Headers.TryAddWithoutValidation(SolverApiAuth.HeaderName, key);

        return base.SendAsync(request, cancellationToken);
    }
}
