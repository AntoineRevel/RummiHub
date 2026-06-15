using RummiHub.Core.Models;
using RummiHub.Core.Solver;
using RummiHub.SolverBench.Comparison;
using RummiHub.SolverBench.Model;
using RummiHub.Solver.Worker.Solver.Dynamic;

namespace RummiHub.SolverBench.Trace;

/// <summary>
/// Generates a reproducible game trace. A light game loop — tile pool, racks, turns, the
/// 30-point opening rule — is run entirely on <c>Core</c> models, with <c>DynamicSolver</c>
/// choosing every move (play if a move exists, otherwise draw). Each turn's position is
/// captured (board + hand + hasPlayed) and tagged by phase. The same seed always yields the
/// same trace, because the shuffle is seeded and the solver is deterministic.
/// </summary>
public static class GameTraceGenerator
{
    private const int TilesPerPlayer = 14;

    public static GameTrace Generate(int seed, GameTraceOptions? options = null)
    {
        options ??= new GameTraceOptions();
        var positions = new List<BenchPosition>();

        for (var game = 0; game < options.Games && positions.Count < options.MaxPositions; game++)
            PlayGame(seed, game, options, positions);

        return new GameTrace { Seed = seed, Positions = positions };
    }

    private static void PlayGame(int seed, int game, GameTraceOptions options, List<BenchPosition> positions)
    {
        var rng = new Random(Combine(seed, game));
        var tiles = GenerateTiles();
        Shuffle(tiles, rng);

        var playerCount = Math.Max(2, options.PlayerCount);
        var racks = new Set[playerCount];
        var hasPlayed = new bool[playerCount];
        var pool = new Queue<Tile>();

        for (var p = 0; p < playerCount; p++)
            racks[p] = new Set(tiles.Skip(p * TilesPerPlayer).Take(TilesPerPlayer).ToList());
        foreach (var tile in tiles.Skip(playerCount * TilesPerPlayer))
            pool.Enqueue(tile);

        var board = new Solution();
        var playerIndex = 0;
        var turn = 0;
        var idleStreak = 0;

        while (turn < options.MaxTurnsPerGame && positions.Count < options.MaxPositions)
        {
            var rack = racks[playerIndex];
            var played = hasPlayed[playerIndex];

            // Solve the current decision point with the baseline.
            ISolver solver = played
                ? DynamicSolver.Create(board.ToSet(), new Set(rack))
                : DynamicSolver.CreateFirst(new Set(rack));
            var result = solver.SearchSolution();

            // Capture the position exactly as the solvers will see it.
            var storedBoard = played ? Clone(board) : new Solution();
            if (result.Found || options.CaptureNoMovePositions)
                positions.Add(BuildPosition(storedBoard, rack, played, result, seed, game, turn, playerIndex));

            // Apply the baseline's choice: play if possible, otherwise draw.
            var didSomething = false;
            if (result.Found)
            {
                ApplyMove(result, rack, ref board, played);
                hasPlayed[playerIndex] = true;
                didSomething = true;
                if (rack.Tiles.Count == 0 && rack.Jokers == 0) return; // winner — game over
            }
            else if (pool.Count > 0)
            {
                rack.AddTile(pool.Dequeue());
                didSomething = true;
            }

            idleStreak = didSomething ? 0 : idleStreak + 1;
            if (idleStreak >= playerCount) return; // everyone stuck (empty pool, no moves)

            playerIndex = (playerIndex + 1) % playerCount;
            if (playerIndex == 0) turn++;
        }
    }

    private static BenchPosition BuildPosition(
        Solution storedBoard, Set rack, bool hasPlayed, SolverResult result,
        int seed, int game, int turn, int playerIndex)
    {
        var hand = rack.GetAllTilesIncludingJokers().ToList();
        var phaseTag = hasPlayed ? "during-game" : "first-play";
        var tags = new List<string> { phaseTag };
        tags.AddRange(PositionInterest.Structural(storedBoard, result));

        return new BenchPosition
        {
            Id = BenchPosition.ComputeId(hand, storedBoard, hasPlayed),
            Name = $"{phaseTag} g{game} t{turn} p{playerIndex}",
            Tags = tags.Distinct().ToList(),
            HasPlayed = hasPlayed,
            Board = storedBoard,
            Hand = hand,
            Provenance = $"trace:seed={seed}:game={game}:turn={turn}:player={playerIndex}",
            ReferenceBest = ReferenceBest.From(result)
        };
    }

    /// <summary>Mirrors GameEngine.ApplySolveResult without the SignalR/UI coupling.</summary>
    private static void ApplyMove(SolverResult result, Set rack, ref Solution board, bool hasPlayed)
    {
        foreach (var tile in result.TilesToPlay)
            rack.Remove(tile);
        for (var i = 0; i < result.JokersToPlay; i++)
            rack.Remove(new Tile(isJoker: true));

        board = hasPlayed
            ? result.BestSolution
            : result.BestSolution.AddSolution(board);
    }

    private static int Combine(int seed, int game) => unchecked(seed * 397 ^ (game + 1));

    private static Solution Clone(Solution s) => new()
    {
        IsValid = s.IsValid,
        Runs = s.Runs.Select(r => new ValidSet(r.Tiles.ToArray())).ToList(),
        Groups = s.Groups.Select(g => new ValidSet(g.Tiles.ToArray())).ToList()
    };

    private static Tile[] GenerateTiles()
    {
        var tiles = new Tile[106];
        var index = 0;
        foreach (var color in Enum.GetValues<TileColor>())
            for (var value = 1; value <= 13; value++)
            {
                tiles[index++] = new Tile(value, color);
                tiles[index++] = new Tile(value, color);
            }

        tiles[index++] = new Tile(isJoker: true);
        tiles[index] = new Tile(isJoker: true);
        return tiles;
    }

    private static void Shuffle(Tile[] tiles, Random rng)
    {
        for (var i = tiles.Length - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }
    }
}
