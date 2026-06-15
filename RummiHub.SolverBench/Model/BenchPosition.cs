using System.Text.Json.Serialization;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;

namespace RummiHub.SolverBench.Model;

/// <summary>
/// A single benchmark position: a real board + hand snapshot that every selected solver
/// is asked to solve. This is also the on-disk bank schema (one JSON file per position).
/// </summary>
public sealed record BenchPosition
{
    /// <summary>Stable content id (16-hex). Identical positions share an id ⇒ stable corpus.</summary>
    public required string Id { get; init; }

    public string Name { get; init; } = "";

    public List<string> Tags { get; init; } = [];

    /// <summary>False ⇒ opening play (first-play solvers); true ⇒ in-game move.</summary>
    public bool HasPlayed { get; init; }

    /// <summary>Board state (Runs + Groups). Empty for a genuine opening play.</summary>
    public required Solution Board { get; init; }

    /// <summary>The player's rack (jokers carried as <c>Tile.IsJoker</c>).</summary>
    public required List<Tile> Hand { get; init; }

    /// <summary>Where the position came from (e.g. <c>trace:seed=…</c>, <c>manual</c>, <c>auto:…</c>).</summary>
    public string Provenance { get; init; } = "";

    /// <summary>The optimal move per the baseline solver (optimality oracle).</summary>
    public ReferenceBest ReferenceBest { get; init; } = ReferenceBest.NotFound;

    public string Notes { get; init; } = "";

    [JsonIgnore]
    public BenchPhase Phase => HasPlayed ? BenchPhase.DuringGame : BenchPhase.FirstPlay;

    /// <summary>Fresh <see cref="Set"/> built from the hand (a private copy per call).</summary>
    public Set HandSet()
    {
        var set = new Set();
        foreach (var tile in Hand) set.AddTile(tile);
        return set;
    }

    /// <summary>
    /// Board as a flat <see cref="Set"/> for during-game solvers. For an opening play the
    /// board is irrelevant to the first-play contract, so callers pass an empty board there.
    /// </summary>
    public Set BoardSet() => Board.ToSet();

    /// <summary>Computes the stable content id for a (hand, board, hasPlayed) triple.</summary>
    public static string ComputeId(List<Tile> hand, Solution board, bool hasPlayed) =>
        ContentHasher.ComputeHash(hand, board, hasPlayed);
}
