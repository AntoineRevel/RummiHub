namespace RummiHub.Core.Contracts;

/// <summary>
/// Shared settings for the internal solver API. The same <see cref="Key"/> must be set on
/// the API and on the web frontend (environment variable <c>SolverApi__Key</c>).
/// </summary>
public sealed class SolverApiOptions
{
    public const string SectionName = "SolverApi";

    /// <summary>Global fixed-window cap on <c>/solve</c> when <see cref="PermitLimit"/> is unset.</summary>
    public const int DefaultPermitLimit = 600;

    public const int DefaultWindowSeconds = 60;

    /// <summary>
    /// Shared secret presented in the <see cref="SolverApiAuth.HeaderName"/> header.
    /// Empty is allowed only when the host environment is Development.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>How many <c>/solve</c> calls are accepted per <see cref="WindowSeconds"/>, across all callers.</summary>
    public int PermitLimit { get; set; } = DefaultPermitLimit;

    public int WindowSeconds { get; set; } = DefaultWindowSeconds;
}
