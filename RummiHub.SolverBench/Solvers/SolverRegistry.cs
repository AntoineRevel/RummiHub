using RummiHub.SolverBench.Model;
using RummiHub.Solver.Worker.Solver.Combinations;
using RummiHub.Solver.Worker.Solver.Combinations.First;
using RummiHub.Solver.Worker.Solver.Dynamic;
using RummiHub.Solver.Worker.Solver.Graph;
using RummiHub.Solver.Worker.Solver.Graph.First;
using RummiHub.Solver.Worker.Solver.Incremental;

namespace RummiHub.SolverBench.Solvers;

/// <summary>
/// The catalogue of solvers that can be compared, split by phase. Mirrors the factory
/// switch in <c>RummiHub.Tests/Solver/ConcreteSolverTests.cs</c>. <c>DynamicSolver</c> is
/// flagged as the baseline in both phases.
/// </summary>
public static class SolverRegistry
{
    public const string BaselineName = "DynamicSolver";

    public static readonly IReadOnlyList<SolverDescriptor> FirstPlay =
    [
        new("GraphFirstSolver", BenchPhase.FirstPlay,
            (_, rack) => GraphFirstSolver.Create(rack)),
        new("IncrementalFirstBaseSolver", BenchPhase.FirstPlay,
            (_, rack) => IncrementalFirstBaseSolver.Create(rack)),
        new("CombinationsFirstSolver", BenchPhase.FirstPlay,
            (_, rack) => CombinationsFirstSolver.Create(rack)),
        new(BaselineName, BenchPhase.FirstPlay,
            (_, rack) => DynamicSolver.CreateFirst(rack)) { IsBaseline = true }
    ];

    public static readonly IReadOnlyList<SolverDescriptor> DuringGame =
    [
        new("GraphSolver", BenchPhase.DuringGame,
            (board, rack) => GraphSolver.Create(board, rack)),
        new("IncrementalComplexSolver", BenchPhase.DuringGame,
            (board, rack) => IncrementalComplexSolver.Create(board, rack)),
        new("CombinationsSolver", BenchPhase.DuringGame,
            (board, rack) => CombinationsSolver.Create(board, rack)),
        new("ParallelCombinationsSolver", BenchPhase.DuringGame,
            (board, rack) => ParallelCombinationsSolver.Create(board, rack)),
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
