using System.Text.Json.Serialization;
using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.SolverBench.Model;

/// <summary>
/// The optimal move as found by the baseline <c>DynamicSolver</c> for a position.
/// Stored with each bank entry so the corpus doubles as a non-regression oracle
/// (same position ⇒ same expected optimum). No solver is treated as absolute truth;
/// this is simply the agreed optimality baseline against which divergences are reported.
/// </summary>
public sealed record ReferenceBest
{
    /// <summary>Whether the baseline found any legal move (a ≥30 opening, or a real in-game move).</summary>
    public bool Found { get; init; }

    /// <summary>Real (non-joker) rack tiles played by the baseline move.</summary>
    public List<Tile> Tiles { get; init; } = [];

    /// <summary>Rack jokers played by the baseline move.</summary>
    public int Jokers { get; init; }

    /// <summary>Score reported by the baseline solver.</summary>
    public int Score { get; init; }

    /// <summary>Total rack tiles moved to the board (reals + jokers) — the primary in-game objective.</summary>
    [JsonIgnore]
    public int RackTilesPlayed => Tiles.Count + Jokers;

    public static readonly ReferenceBest NotFound = new() { Found = false };

    /// <summary>
    /// Builds a reference from a solver result, normalizing joker accounting (some solvers
    /// place joker tiles inside <c>TilesToPlay</c>, others count them via <c>JokersToPlay</c>).
    /// </summary>
    public static ReferenceBest From(SolverResult result)
    {
        if (!result.Found) return NotFound;

        var realTiles = result.TilesToPlay.Where(t => !t.IsJoker).ToList();
        var jokers = result.JokersToPlay + result.TilesToPlay.Count(t => t.IsJoker);

        return new ReferenceBest
        {
            Found = true,
            Tiles = realTiles,
            Jokers = jokers,
            Score = result.Score
        };
    }
}
