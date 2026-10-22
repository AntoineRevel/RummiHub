namespace RummiHub.Core.Contracts;

/// <summary>
/// Shared settings for the internal solver API. The same <see cref="Key"/> must be set on
/// the API and on the web frontend (environment variable <c>SolverApi__Key</c>).
/// </summary>
public sealed class SolverApiOptions
{
    public const string SectionName = "SolverApi";

    /// <summary>
    /// Shared secret presented in the <see cref="SolverApiAuth.HeaderName"/> header.
    /// Empty is allowed only when the host environment is Development.
    /// </summary>
    public string? Key { get; set; }
}
