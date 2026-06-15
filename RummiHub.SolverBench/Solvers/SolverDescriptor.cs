using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Solvers;

/// <summary>
/// A selectable solver: its display name, the phase it applies to, and a factory that
/// builds a fresh instance for a position. The factory always receives <b>copies</b> of
/// the sets, because several solvers sort/mutate their input in place.
/// </summary>
public sealed class SolverDescriptor(string name, BenchPhase phase, Func<Set, Set, ISolver> factory)
{
    public string Name { get; } = name;
    public BenchPhase Phase { get; } = phase;

    /// <summary>True for the polynomial baseline used as the optimality oracle.</summary>
    public bool IsBaseline { get; init; }

    /// <summary>Builds a single-use solver from a position, copying the sets first.</summary>
    public ISolver Create(BenchPosition position)
    {
        var hand = position.HandSet();
        // First-play solvers ignore the board by contract; pass an empty board to them.
        var board = position.Phase == BenchPhase.FirstPlay ? new Set() : position.BoardSet();
        return factory(board, hand);
    }
}
