using RummiHub.Core.Models;
using RummiHub.Core.Solver;

namespace RummiHub.Solver.Worker.Solver.Dynamic;

/// <summary>
/// Exact, polynomial-time Rummikub solver based on the dynamic-programming
/// formulation of D. den Hertog &amp; P. B. Hulshof, "Solving Rummikub Problems by
/// Integer Linear Programming" (The Computer Journal, 2006).
///
/// <para>
/// Unlike the exponential tree/graph search, this sweeps the 13 values once, from low
/// to high, carrying a tiny bounded state: for every color, how many runs are currently
/// "open" with length 1, 2 or ≥3 (an open run of length 1 or 2 MUST be extended; a run
/// of length ≥3 may be closed for free or extended). Groups never span values, so they
/// are resolved locally at each value. Jokers are a shared budget carried in the state.
/// Because at most two copies of every tile exist, the run-state is bounded by a constant,
/// which makes the whole search polynomial in the number of tiles.
/// </para>
///
/// <para>
/// The same engine serves both phases. During the game every board tile is forced to be
/// played (huge per-tile weight) and the objective maximizes the number of rack tiles
/// played, then the score. For the first play there is no board and the objective is the
/// raw score, gated at <see cref="ISolver.MinScore"/>.
/// </para>
/// </summary>
public sealed class DynamicSolver : ISolver
{
    private const int Colors = 4;
    private const int MaxValue = 13;

    // A placed board tile is worth far more than any achievable rack contribution, so the
    // optimum always keeps every board tile on the table (the untouched board is always a
    // feasible baseline). Kept well below long.MaxValue even for a fully packed table.
    private const long BoardTileWeight = 10_000_000L;

    // One extra rack tile played outranks any value-based tiebreak (max total value < 1000).
    private const long DuringGameCountUnit = 10_000L;

    // ── precomputed, input-independent tables ──────────────────────────────────────
    private static readonly (int A, int B, int C)[] ColorStates;
    private static readonly int[,,] ColorStateIndex;
    private static readonly RunDecision[][] RunDecisionsByState;
    private static readonly GroupProfile[] GroupProfiles;

    // ── per-instance input ─────────────────────────────────────────────────────────
    private readonly int[,] _board = new int[MaxValue + 1, Colors];
    private readonly int[,] _rack = new int[MaxValue + 1, Colors];
    private readonly int[,] _count = new int[MaxValue + 1, Colors];
    private readonly int _boardJokers;
    private readonly int _totalJokers;
    private readonly long _countUnit;
    private readonly bool _firstPlay;
    private readonly int _jokerStates; // _totalJokers + 1

    static DynamicSolver()
    {
        ColorStates = BuildColorStates();
        ColorStateIndex = BuildColorStateIndex(ColorStates);
        RunDecisionsByState = BuildRunDecisions(ColorStates, ColorStateIndex);
        GroupProfiles = BuildGroupProfiles();
    }

    private DynamicSolver(bool firstPlay, int boardJokers, int totalJokers)
    {
        _firstPlay = firstPlay;
        _boardJokers = boardJokers;
        _totalJokers = totalJokers;
        _jokerStates = totalJokers + 1;
        _countUnit = firstPlay ? 0L : DuringGameCountUnit;
    }

    /// <summary>During-game solver: keeps every board tile, maximizes rack tiles then score.</summary>
    public static DynamicSolver Create(Set board, Set rack)
    {
        var solver = new DynamicSolver(firstPlay: false, board.Jokers, board.Jokers + rack.Jokers);
        solver.Fill(solver._board, board.Tiles);
        solver.Fill(solver._rack, rack.Tiles);
        solver.Combine();
        return solver;
    }

    /// <summary>First-play solver: no board, maximizes score, gated at <see cref="ISolver.MinScore"/>.</summary>
    public static DynamicSolver CreateFirst(Set rack)
    {
        var solver = new DynamicSolver(firstPlay: true, boardJokers: 0, totalJokers: rack.Jokers);
        solver.Fill(solver._rack, rack.Tiles);
        solver.Combine();
        return solver;
    }

    private void Fill(int[,] target, List<Tile> tiles)
    {
        foreach (var tile in tiles)
        {
            if (tile.IsJoker) continue;
            var v = tile.Value;
            if (v is < 1 or > MaxValue) continue;
            target[v, (int)tile.Color]++;
        }
    }

    private void Combine()
    {
        for (var v = 1; v <= MaxValue; v++)
        for (var k = 0; k < Colors; k++)
            _count[v, k] = _board[v, k] + _rack[v, k];
    }

    public SolverResult SearchSolution(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return SolverResult.Invalid(nameof(DynamicSolver));

        var objByState = new Dictionary<int, long>[MaxValue + 1];
        var backByState = new Dictionary<int, Back>[MaxValue + 1];

        objByState[0] = new Dictionary<int, long> { [Encode(0, 0, 0, 0, _totalJokers)] = 0L };
        backByState[0] = new Dictionary<int, Back>();

        for (var v = 1; v <= MaxValue; v++)
        {
            if (cancellationToken.IsCancellationRequested)
                return SolverResult.Invalid(nameof(DynamicSolver));

            var prev = objByState[v - 1];
            var next = new Dictionary<int, long>(prev.Count * 4);
            var nextBack = new Dictionary<int, Back>(prev.Count * 4);

            foreach (var (state, baseObj) in prev)
                Expand(v, state, baseObj, next, nextBack);

            objByState[v] = next;
            backByState[v] = nextBack;
        }

        var terminal = SelectTerminal(objByState[MaxValue]);
        if (terminal is null)
            return SolverResult.Invalid(nameof(DynamicSolver));

        return Reconstruct(terminal.Value, backByState);
    }

    /// <summary>Expands one DP state at value <paramref name="v"/> over every run/group decision.</summary>
    private void Expand(int v, int state, long baseObj, Dictionary<int, long> next, Dictionary<int, Back> nextBack)
    {
        var jokers = state % _jokerStates;
        var rs = state / _jokerStates;
        Span<int> cs = stackalloc int[Colors];
        cs[0] = rs % 10;
        cs[1] = rs / 10 % 10;
        cs[2] = rs / 100 % 10;
        cs[3] = rs / 1000 % 10;

        var d0 = RunDecisionsByState[cs[0]];
        var d1 = RunDecisionsByState[cs[1]];
        var d2 = RunDecisionsByState[cs[2]];
        var d3 = RunDecisionsByState[cs[3]];

        foreach (var r0 in d0)
        {
            if (Infeasible(v, 0, r0.Rdem, jokers)) continue;
            foreach (var r1 in d1)
            {
                if (Infeasible(v, 1, r1.Rdem, jokers)) continue;
                foreach (var r2 in d2)
                {
                    if (Infeasible(v, 2, r2.Rdem, jokers)) continue;
                    foreach (var r3 in d3)
                    {
                        if (Infeasible(v, 3, r3.Rdem, jokers)) continue;

                        for (var gp = 0; gp < GroupProfiles.Length; gp++)
                        {
                            var profile = GroupProfiles[gp];
                            var g = profile.Gdem;

                            var jokerNeed =
                                JokerFill(v, 0, r0.Rdem + g[0]) +
                                JokerFill(v, 1, r1.Rdem + g[1]) +
                                JokerFill(v, 2, r2.Rdem + g[2]) +
                                JokerFill(v, 3, r3.Rdem + g[3]);
                            if (jokerNeed > jokers) continue;

                            var inc =
                                CellObjective(v, 0, r0.Rdem + g[0]) +
                                CellObjective(v, 1, r1.Rdem + g[1]) +
                                CellObjective(v, 2, r2.Rdem + g[2]) +
                                CellObjective(v, 3, r3.Rdem + g[3]);

                            var newState = Encode(r0.Out, r1.Out, r2.Out, r3.Out, jokers - jokerNeed);
                            var newObj = baseObj + inc;

                            if (next.TryGetValue(newState, out var existing) && existing >= newObj) continue;

                            next[newState] = newObj;
                            nextBack[newState] = new Back(state, r0, r1, r2, r3, gp);
                        }
                    }
                }
            }
        }
    }

    /// <summary>A run demand that needs more jokers than exist (regardless of groups) is impossible.</summary>
    private bool Infeasible(int v, int k, int rdem, int jokers)
        => rdem - _count[v, k] > jokers;

    private int JokerFill(int v, int k, int slots)
    {
        var deficit = slots - _count[v, k];
        return deficit > 0 ? deficit : 0;
    }

    private long CellObjective(int v, int k, int slots)
    {
        if (slots == 0) return 0L;
        var real = Math.Min(slots, _count[v, k]);
        var boardUsed = Math.Min(real, _board[v, k]);
        var rackUsed = real - boardUsed;
        var jokerUsed = slots - real;
        return boardUsed * BoardTileWeight + rackUsed * (_countUnit + v) + jokerUsed * (long)v;
    }

    /// <summary>
    /// Picks the best terminal state: no run may still be open with length 1 or 2 (they
    /// could never reach the legal length of 3), and every board joker must have been used.
    /// </summary>
    private int? SelectTerminal(Dictionary<int, long> last)
    {
        int? best = null;
        var bestObj = long.MinValue;
        var rackJokers = _totalJokers - _boardJokers;

        foreach (var (state, obj) in last)
        {
            var jokers = state % _jokerStates;
            if (jokers > rackJokers) continue; // board jokers not all played

            var rs = state / _jokerStates;
            if (HasUnfinishedRun(rs % 10) || HasUnfinishedRun(rs / 10 % 10) ||
                HasUnfinishedRun(rs / 100 % 10) || HasUnfinishedRun(rs / 1000 % 10))
                continue;

            if (obj <= bestObj) continue;
            bestObj = obj;
            best = state;
        }

        return best;
    }

    private static bool HasUnfinishedRun(int colorStateIndex)
    {
        var (a, b, _) = ColorStates[colorStateIndex];
        return a > 0 || b > 0;
    }

    // ── reconstruction ──────────────────────────────────────────────────────────────

    private SolverResult Reconstruct(int terminalState, Dictionary<int, Back>[] backByState)
    {
        var decisions = new Back[MaxValue + 1];
        var cursor = terminalState;
        for (var v = MaxValue; v >= 1; v--)
        {
            var back = backByState[v][cursor];
            decisions[v] = back;
            cursor = back.Prev;
        }

        var solution = new Solution { IsValid = true };
        var openRuns = new List<List<Tile>>[Colors];
        for (var k = 0; k < Colors; k++) openRuns[k] = [];

        var tilesToPlay = new List<Tile>();

        for (var v = 1; v <= MaxValue; v++)
            ApplyValue(v, decisions[v], openRuns, solution, tilesToPlay);

        // Every run still open at value 13 has length ≥ 3 (guaranteed by SelectTerminal): close it.
        foreach (var runsOfColor in openRuns)
        foreach (var run in runsOfColor)
            solution.AddRun(new ValidSet([.. run]));

        var jokersUsed = _totalJokers - terminalState % _jokerStates;
        var rackJokersPlayed = jokersUsed - _boardJokers;
        var score = solution.Runs.Sum(s => s.GetScore()) + solution.Groups.Sum(s => s.GetScore());

        if (_firstPlay)
        {
            // Below the first-play threshold there is no legal move, but report the best score
            // the rack could reach so the UI can show it (e.g. "Current score: 27") instead of 0.
            if (score < ISolver.MinScore) return SolverResult.Invalid(nameof(DynamicSolver), score);
        }
        else if (tilesToPlay.Count == 0 && rackJokersPlayed == 0)
        {
            return SolverResult.Invalid(nameof(DynamicSolver));
        }

        return SolverResult.FromSolution(nameof(DynamicSolver), solution, tilesToPlay, rackJokersPlayed, score);
    }

    /// <summary>Replays the decision taken at one value: extends/closes/opens runs and emits groups.</summary>
    private void ApplyValue(int v, Back back, List<List<Tile>>[] openRuns, Solution solution, List<Tile> tilesToPlay)
    {
        var profile = GroupProfiles[back.Group];

        for (var k = 0; k < Colors; k++)
        {
            var decision = back.Run(k);
            var runs = openRuns[k];

            // Order the open runs so length-1/2 runs (which MUST be extended) come first,
            // then length-≥3 runs (of which we extend exactly E and close the rest).
            runs.Sort(static (x, y) => x.Count.CompareTo(y.Count));

            var mustExtend = 0;
            foreach (var run in runs)
                if (run.Count < 3)
                    mustExtend++;

            var longRuns = runs.Count - mustExtend;
            var longToExtend = decision.E;
            var longToClose = longRuns - longToExtend;

            var slots = decision.Rdem + profile.Gdem[k];
            var realAvailable = Math.Min(slots, _count[v, k]);
            var boardReals = Math.Min(realAvailable, _board[v, k]);
            var rackReals = realAvailable - boardReals;
            for (var i = 0; i < rackReals; i++) tilesToPlay.Add(new Tile(v, (TileColor)k));

            var realsLeft = realAvailable;
            Tile NextRunTile() => Take(ref realsLeft, v, k);

            var stillOpen = new List<List<Tile>>(runs.Count + decision.N);

            for (var i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                if (run.Count < 3)
                {
                    run.Add(NextRunTile());
                    stillOpen.Add(run);
                }
                else if (longToExtend > 0)
                {
                    run.Add(NextRunTile());
                    stillOpen.Add(run);
                    longToExtend--;
                }
                else if (longToClose > 0)
                {
                    solution.AddRun(new ValidSet([.. run]));
                    longToClose--;
                }
                else
                {
                    run.Add(NextRunTile());
                    stillOpen.Add(run);
                }
            }

            for (var i = 0; i < decision.N; i++)
                stillOpen.Add([NextRunTile()]);

            openRuns[k] = stillOpen;
            _reconstructRealsLeft[k] = realsLeft; // reals not consumed by runs go to groups
        }

        // Groups for this value draw their reals from whatever each color did not spend on runs.
        foreach (var group in profile.Groups)
        {
            var tiles = new Tile[group.Length];
            for (var i = 0; i < group.Length; i++)
            {
                var k = group[i];
                tiles[i] = Take(ref _reconstructRealsLeft[k], v, k);
            }

            solution.AddGroup(new ValidSet(tiles));
        }
    }

    private readonly int[] _reconstructRealsLeft = new int[Colors];

    private static Tile Take(ref int realsLeft, int v, int k)
    {
        if (realsLeft > 0)
        {
            realsLeft--;
            return new Tile(v, (TileColor)k);
        }

        return new Tile(v, (TileColor)k, isJoker: true);
    }

    // ── encoding ─────────────────────────────────────────────────────────────────────

    private int Encode(int cs0, int cs1, int cs2, int cs3, int jokers)
        => ((cs0 + cs1 * 10 + cs2 * 100 + cs3 * 1000) * _jokerStates) + jokers;

    // ── table construction (runs once, statically) ────────────────────────────────────

    private static (int, int, int)[] BuildColorStates()
    {
        var list = new List<(int, int, int)>();
        for (var a = 0; a <= 2; a++)
        for (var b = 0; a + b <= 2; b++)
        for (var c = 0; a + b + c <= 2; c++)
            list.Add((a, b, c));
        return list.ToArray();
    }

    private static int[,,] BuildColorStateIndex((int A, int B, int C)[] states)
    {
        var index = new int[3, 3, 3];
        for (var a = 0; a < 3; a++)
        for (var b = 0; b < 3; b++)
        for (var c = 0; c < 3; c++)
            index[a, b, c] = -1;

        for (var i = 0; i < states.Length; i++)
            index[states[i].A, states[i].B, states[i].C] = i;

        return index;
    }

    private static RunDecision[][] BuildRunDecisions((int A, int B, int C)[] states, int[,,] index)
    {
        var result = new RunDecision[states.Length][];

        for (var i = 0; i < states.Length; i++)
        {
            var (a, b, c) = states[i];
            var decisions = new List<RunDecision>();

            // length-1 and length-2 runs MUST be extended; long runs: extend E of C; start N new.
            for (var e = 0; e <= c; e++)
            for (var n = 0; n + a + b + e <= 2; n++)
            {
                var outIndex = index[n, a, b + e];
                var rdem = a + b + e + n;
                decisions.Add(new RunDecision(rdem, outIndex, e, n));
            }

            result[i] = decisions.ToArray();
        }

        return result;
    }

    private static GroupProfile[] BuildGroupProfiles()
    {
        // Every legal group of a single value is a set of 3 or 4 distinct colors.
        var colorSets = new List<int[]>();
        for (var mask = 1; mask < 1 << Colors; mask++)
        {
            var bits = System.Numerics.BitOperations.PopCount((uint)mask);
            if (bits is < 3 or > 4) continue;

            var set = new List<int>();
            for (var k = 0; k < Colors; k++)
                if ((mask & (1 << k)) != 0)
                    set.Add(k);
            colorSets.Add(set.ToArray());
        }

        // At most two groups of the same value can exist (only two copies of each tile).
        var byGdem = new Dictionary<string, GroupProfile>();

        void Add(int[][] groups)
        {
            var gdem = new int[Colors];
            foreach (var group in groups)
            foreach (var k in group)
                gdem[k]++;
            if (gdem.Any(x => x > 2)) return;

            var key = $"{gdem[0]}{gdem[1]}{gdem[2]}{gdem[3]}";
            byGdem.TryAdd(key, new GroupProfile(gdem, groups));
        }

        Add([]);
        foreach (var s in colorSets) Add([s]);
        for (var i = 0; i < colorSets.Count; i++)
        for (var j = i; j < colorSets.Count; j++)
            Add([colorSets[i], colorSets[j]]);

        return byGdem.Values.ToArray();
    }

    private readonly record struct RunDecision(int Rdem, int Out, int E, int N);

    private sealed class GroupProfile(int[] gdem, int[][] groups)
    {
        public int[] Gdem { get; } = gdem;
        public int[][] Groups { get; } = groups;
    }

    private sealed class Back(int prev, RunDecision r0, RunDecision r1, RunDecision r2, RunDecision r3, int group)
    {
        public int Prev { get; } = prev;
        public int Group { get; } = group;
        private readonly RunDecision _r0 = r0, _r1 = r1, _r2 = r2, _r3 = r3;

        public RunDecision Run(int k) => k switch
        {
            0 => _r0,
            1 => _r1,
            2 => _r2,
            _ => _r3
        };
    }
}
