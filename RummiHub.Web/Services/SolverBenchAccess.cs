namespace RummiHub.Web.Services;

/// <summary>
/// Whether the <c>/bench</c> developer page is served. The page runs CPU-heavy solver
/// comparisons inside the web process and writes the move bank to disk, so on a public
/// host any visitor could saturate the server. It is off outside Development unless
/// <c>SolverBench:Enabled</c> (<c>SolverBench__Enabled</c>) is set to true.
/// </summary>
public sealed record SolverBenchAccess(bool Enabled)
{
    public const string ConfigKey = "SolverBench:Enabled";
    public const string PagePath = "/bench";

    public static SolverBenchAccess From(IConfiguration configuration, IHostEnvironment environment) =>
        new(configuration.GetValue(ConfigKey, environment.IsDevelopment()));
}
