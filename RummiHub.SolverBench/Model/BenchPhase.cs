namespace RummiHub.SolverBench.Model;

/// <summary>
/// Which solving phase a position belongs to. Derived from <c>HasPlayed</c>:
/// a player who has not yet made their opening 30-point play is in
/// <see cref="FirstPlay"/>; afterwards every move is <see cref="DuringGame"/>.
/// First-play positions only go to first-play solvers, and vice-versa.
/// </summary>
public enum BenchPhase
{
    FirstPlay,
    DuringGame
}
