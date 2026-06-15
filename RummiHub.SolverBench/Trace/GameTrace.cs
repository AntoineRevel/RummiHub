using RummiHub.SolverBench.Model;

namespace RummiHub.SolverBench.Trace;

/// <summary>Options for generating a reference game trace.</summary>
public sealed record GameTraceOptions
{
    public int PlayerCount { get; init; } = 2;

    /// <summary>Chain several seeded games to grow the corpus.</summary>
    public int Games { get; init; } = 1;

    /// <summary>Safety cap on turns per game (prevents pathological stalls).</summary>
    public int MaxTurnsPerGame { get; init; } = 80;

    /// <summary>Stop once this many positions have been captured across all games.</summary>
    public int MaxPositions { get; init; } = int.MaxValue;

    /// <summary>Also capture positions where the baseline found no move (the player draws).</summary>
    public bool CaptureNoMovePositions { get; init; } = true;
}

/// <summary>A reproducible sequence of real positions produced by a seeded game.</summary>
public sealed record GameTrace
{
    public required int Seed { get; init; }
    public required IReadOnlyList<BenchPosition> Positions { get; init; }

    public int FirstPlayCount => Positions.Count(p => p.Phase == BenchPhase.FirstPlay);
    public int DuringGameCount => Positions.Count(p => p.Phase == BenchPhase.DuringGame);
}
