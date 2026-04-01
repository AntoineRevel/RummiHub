namespace RummiHub.Core.Solver;

public interface ISolver
{
    const int MinScore = 30;
    SolverResult SearchSolution(CancellationToken cancellationToken = default);
}
