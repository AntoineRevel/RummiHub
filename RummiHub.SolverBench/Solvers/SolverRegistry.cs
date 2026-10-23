using RummiHub.SolverBench.Model;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;

namespace RummiHub.SolverBench.Solvers;

/// <summary>
/// Solvers that can be compared, split by phase. <see cref="DynamicSolver"/> is the baseline.
/// <c>GraphSolver</c> / <c>GraphFirstSolver</c> are the production fallback.
/// </summary>
public static class SolverRegistry
{
    public const string BaselineName = "DynamicSolver";

    public static readonly IReadOnlyList<SolverDescriptor> FirstPlay =
    [
        new("GraphFirstSolver", BenchPhase.FirstPlay,
            (_, rack) => GraphFirstSolver.Create(rack)),
        new(BaselineName, BenchPhase.FirstPlay,
            (_, rack) => DynamicSolver.CreateFirst(rack)) { IsBaseline = true }
    ];

    public static readonly IReadOnlyList<SolverDescriptor> DuringGame =
    [
        new("GraphSolver", BenchPhase.DuringGame,
            (board, rack) => GraphSolver.Create(board, rack)),
        new(BaselineName, BenchPhase.DuringGame,
            (board, rack) => DynamicSolver.Create(board, rack)) { IsBaseline = true }
    ];

    public static IReadOnlyList<SolverDescriptor> All =>
        [.. FirstPlay, .. DuringGame];

    public static IReadOnlyList<SolverDescriptor> ForPhase(BenchPhase phase) =>
        phase == BenchPhase.FirstPlay ? FirstPlay : DuringGame;

    /// <summary>The baseline descriptor for a phase (the optimality oracle).</summary>
    public static SolverDescriptor Baseline(BenchPhase phase) =>
        ForPhase(phase).First(s => s.IsBaseline);

    /// <summary>Resolves descriptors by name within a phase (used by UI / CLI selection).</summary>
    public static IReadOnlyList<SolverDescriptor> Select(BenchPhase phase, IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ForPhase(phase).Where(s => wanted.Contains(s.Name)).ToList();
    }
}
