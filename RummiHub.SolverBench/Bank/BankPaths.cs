namespace RummiHub.SolverBench.Bank;

/// <summary>
/// Resolves the versioned bank directory (<c>solver-bench/bank</c>) by walking up from the
/// running assembly to the repository root (marked by <c>RummiHub.slnx</c>). This lets the
/// Blazor app and the headless runner share the exact same on-disk corpus.
/// </summary>
public static class BankPaths
{
    public const string RepoFolder = "solver-bench";
    public const string BankFolder = "bank";

    public static string Default()
    {
        var root = FindRepoRoot(AppContext.BaseDirectory) ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, RepoFolder, BankFolder);
    }

    private static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RummiHub.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
