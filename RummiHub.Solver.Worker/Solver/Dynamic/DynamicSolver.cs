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
/// The sweep runs on flat tables indexed by state and kept per thread, and at each value it
/// only tries, for every run decision, the best group profile per number of jokers spent:
/// the others lead to the same state with a lower objective.
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

    // A tile cell serves two runs and two groups at most.
    private const int MaxSlots = 4;

    // Run demand per color is 0, 1 or 2: 3^4 combinations.
    private const int RunDemands = 81;

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
    private static readonly int[] GroupDemand; // GroupProfiles[gp].Gdem[k] at gp * Colors + k

    // ── per-instance input ─────────────────────────────────────────────────────────
    private readonly int[,] _board = new int[MaxValue + 1, Colors];
    private readonly int[,] _rack = new int[MaxValue + 1, Colors];
    private readonly int[,] _count = new int[MaxValue + 1, Colors];
    private readonly int _boardJokers;
    private readonly int _totalJokers;
    private readonly long _countUnit;
    private readonly bool _firstPlay;
    private readonly int _jokerStates; // _totalJokers + 1

    // Per value and (color, slots): the objective a cell brings and the jokers it needs.
    private readonly long[] _cellGain = new long[Colors * (MaxSlots + 1)];
    private readonly int[] _cellJokers = new int[Colors * (MaxSlots + 1)];

    static DynamicSolver()
    {
        ColorStates = BuildColorStates();
        ColorStateIndex = BuildColorStateIndex(ColorStates);
        RunDecisionsByState = BuildRunDecisions(ColorStates, ColorStateIndex);
        GroupProfiles = BuildGroupProfiles();
        GroupDemand = GroupProfiles.SelectMany(p => p.Gdem).ToArray();
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

        var ws = Workspace.Rent(_jokerStates);
        try
        {
            var start = Encode(0, 0, 0, 0, _totalJokers);
            ws.Objective[start] = 0L;
            ws.States[0] = start;
            var count = 1;

            for (var v = 1; v <= MaxValue; v++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ws.Forget(count);
                    return SolverResult.Invalid(nameof(DynamicSolver));
                }

                PrepareValue(v, ws);

                var back = ws.Back[v];
                var nextCount = 0;
                for (var i = 0; i < count; i++)
                    nextCount = Expand(v, ws.States[i], ws, back, nextCount);

                ws.Forget(count);
                ws.Advance();
                count = nextCount;
            }

            var terminal = SelectTerminal(ws.Objective, ws.States, count);
            ws.Forget(count);

            return terminal is null
                ? SolverResult.Invalid(nameof(DynamicSolver))
                : Reconstruct(terminal.Value, ws.Back);
        }
        catch
        {
            // The tables are in an unknown state: never hand them to the next solve.
            Workspace.Discard();
            throw;
        }
    }

    /// <summary>
    /// What one tile cell of value <paramref name="v"/> brings and costs in jokers for 0 to 4
    /// slots (two runs and two groups at most), and a clean slate for the moves of this value.
    /// </summary>
    private void PrepareValue(int v, Workspace ws)
    {
        for (var k = 0; k < Colors; k++)
        for (var slots = 0; slots <= MaxSlots; slots++)
        {
            var real = Math.Min(slots, _count[v, k]);
            var boardUsed = Math.Min(real, _board[v, k]);
            var rackUsed = real - boardUsed;
            var jokerUsed = slots - real;
            var cell = k * (MaxSlots + 1) + slots;
            _cellJokers[cell] = jokerUsed;
            _cellGain[cell] = boardUsed * BoardTileWeight + rackUsed * (_countUnit + v) + jokerUsed * (long)v;
        }

        Array.Clear(ws.MovesReady);
    }

    /// <summary>
    /// For one run demand per color, the best group profile for every number of jokers the
    /// value may spend. A state only reaches its successors through the jokers left, so every
    /// other profile is dominated: the sweep tries one move per joker count, not one per profile.
    /// </summary>
    private void BuildMoves(Workspace ws, int demand)
    {
        var first = demand * _jokerStates;
        for (var need = 0; need < _jokerStates; need++)
            ws.MoveGain[first + need] = long.MinValue;

        var r0 = demand % 3;
        var r1 = demand / 3 % 3 + (MaxSlots + 1);
        var r2 = demand / 9 % 3 + 2 * (MaxSlots + 1);
        var r3 = demand / 27 + 3 * (MaxSlots + 1);

        for (var gp = 0; gp < GroupProfiles.Length; gp++)
        {
            var g = gp * Colors;
            var c0 = r0 + GroupDemand[g];
            var c1 = r1 + GroupDemand[g + 1];
            var c2 = r2 + GroupDemand[g + 2];
            var c3 = r3 + GroupDemand[g + 3];

            var need = _cellJokers[c0] + _cellJokers[c1] + _cellJokers[c2] + _cellJokers[c3];
            if (need >= _jokerStates) continue;

            var gain = _cellGain[c0] + _cellGain[c1] + _cellGain[c2] + _cellGain[c3];
            if (gain <= ws.MoveGain[first + need]) continue;

            ws.MoveGain[first + need] = gain;
            ws.MoveGroup[first + need] = (byte)gp;
        }

        ws.MovesReady[demand] = true;
    }

    /// <summary>Expands one DP state at value <paramref name="v"/> over every run/group decision.</summary>
    private int Expand(int v, int state, Workspace ws, long[] back, int nextCount)
    {
        var jokerStates = _jokerStates;
        var jokers = state % jokerStates;
        var rs = state / jokerStates;
        var baseObj = ws.Objective[state];

        var d0 = RunDecisionsByState[rs % 10];
        var d1 = RunDecisionsByState[rs / 10 % 10];
        var d2 = RunDecisionsByState[rs / 100 % 10];
        var d3 = RunDecisionsByState[rs / 1000 % 10];

        // A run demand that needs more jokers than are left (whatever the groups do) is impossible.
        var max0 = _count[v, 0] + jokers;
        var max1 = _count[v, 1] + jokers;
        var max2 = _count[v, 2] + jokers;
        var max3 = _count[v, 3] + jokers;

        var next = ws.NextObjective;
        var nextStates = ws.NextStates;
        var moveGain = ws.MoveGain;

        for (var i0 = 0; i0 < d0.Length; i0++)
        {
            var r0 = d0[i0];
            if (r0.Rdem > max0) continue;
            for (var i1 = 0; i1 < d1.Length; i1++)
            {
                var r1 = d1[i1];
                if (r1.Rdem > max1) continue;
                for (var i2 = 0; i2 < d2.Length; i2++)
                {
                    var r2 = d2[i2];
                    if (r2.Rdem > max2) continue;
                    for (var i3 = 0; i3 < d3.Length; i3++)
                    {
                        var r3 = d3[i3];
                        if (r3.Rdem > max3) continue;

                        var demand = r0.Rdem + 3 * r1.Rdem + 9 * r2.Rdem + 27 * r3.Rdem;
                        if (!ws.MovesReady[demand]) BuildMoves(ws, demand);

                        var first = demand * jokerStates;
                        var target = (r0.Out + r1.Out * 10 + r2.Out * 100 + r3.Out * 1000) * jokerStates + jokers;
                        var decisions = (long)(i0 | i1 << 3 | i2 << 6 | i3 << 9) << BackDecisionShift;

                        for (var need = 0; need <= jokers; need++)
                        {
                            var gain = moveGain[first + need];
                            if (gain == long.MinValue) continue;

                            var newState = target - need;
                            var newObj = baseObj + gain;
                            var existing = next[newState];
                            if (existing >= newObj) continue;

                            if (existing == long.MinValue) nextStates[nextCount++] = newState;
                            next[newState] = newObj;
                            back[newState] = (uint)state | decisions |
                                             (long)ws.MoveGroup[first + need] << BackGroupShift;
                        }
                    }
                }
            }
        }

        return nextCount;
    }

    /// <summary>
    /// Picks the best terminal state: no run may still be open with length 1 or 2 (they
    /// could never reach the legal length of 3), and every board joker must have been used.
    /// </summary>
    private int? SelectTerminal(long[] objective, int[] states, int count)
    {
        int? best = null;
        var bestObj = long.MinValue;
        var rackJokers = _totalJokers - _boardJokers;

        for (var i = 0; i < count; i++)
        {
            var state = states[i];
            var jokers = state % _jokerStates;
            if (jokers > rackJokers) continue; // board jokers not all played

            var rs = state / _jokerStates;
            if (HasUnfinishedRun(rs % 10) || HasUnfinishedRun(rs / 10 % 10) ||
                HasUnfinishedRun(rs / 100 % 10) || HasUnfinishedRun(rs / 1000 % 10))
                continue;

            var obj = objective[state];
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

    private SolverResult Reconstruct(int terminalState, long[][] backByValue)
    {
        var decisions = new Back[MaxValue + 1];
        var cursor = terminalState;
        for (var v = MaxValue; v >= 1; v--)
        {
            var back = Decode(backByValue[v][cursor]);
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
            solution.Sets.Add(new ValidSet([.. run]));

        var jokersUsed = _totalJokers - terminalState % _jokerStates;
        var rackJokersPlayed = jokersUsed - _boardJokers;
        var score = solution.Sets.Sum(s => s.GetScore());

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
                    solution.Sets.Add(new ValidSet([.. run]));
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

            solution.Sets.Add(new ValidSet(tiles));
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

    /// <summary>How a state was reached: the state before it, one run decision per color, the groups.</summary>
    private readonly record struct Back(int Prev, RunDecision R0, RunDecision R1, RunDecision R2, RunDecision R3, int Group)
    {
        public RunDecision Run(int k) => k switch
        {
            0 => R0,
            1 => R1,
            2 => R2,
            _ => R3
        };
    }

    // A back-pointer packed in a long: the previous state, then the index of each color's run
    // decision (6 at most, 3 bits each), then the group profile.
    private const int BackDecisionShift = 32;
    private const int BackGroupShift = BackDecisionShift + 3 * Colors;

    private Back Decode(long packed)
    {
        var prev = (int)(uint)packed;
        var rs = prev / _jokerStates;
        var decisions = (int)(packed >> BackDecisionShift);
        return new Back(prev,
            RunDecisionsByState[rs % 10][decisions & 7],
            RunDecisionsByState[rs / 10 % 10][decisions >> 3 & 7],
            RunDecisionsByState[rs / 100 % 10][decisions >> 6 & 7],
            RunDecisionsByState[rs / 1000 % 10][decisions >> 9 & 7],
            (int)(packed >> BackGroupShift));
    }

    /// <summary>
    /// The tables of one sweep, indexed by state, kept per thread so a solve allocates next to
    /// nothing (the stabilizer runs hundreds of small solves per move). Between two solves
    /// every objective is back to "unreached".
    /// </summary>
    private sealed class Workspace
    {
        private const int RunStates = 10_000; // ten states per color

        // Two jokers exist; anything beyond a few is a made-up rack, served by a throwaway.
        private const int MaxKeptJokerStates = 4;

        [ThreadStatic] private static Workspace? _kept;

        private readonly int _jokerStates;

        public long[] Objective;
        public long[] NextObjective;
        public int[] States;
        public int[] NextStates;
        public readonly long[][] Back = new long[MaxValue + 1][];
        public readonly long[] MoveGain;
        public readonly byte[] MoveGroup;
        public readonly bool[] MovesReady = new bool[RunDemands];

        private Workspace(int jokerStates)
        {
            _jokerStates = jokerStates;
            var states = RunStates * jokerStates;
            Objective = new long[states];
            NextObjective = new long[states];
            Array.Fill(Objective, long.MinValue);
            Array.Fill(NextObjective, long.MinValue);
            States = new int[states];
            NextStates = new int[states];
            for (var v = 1; v <= MaxValue; v++) Back[v] = new long[states];
            MoveGain = new long[RunDemands * jokerStates];
            MoveGroup = new byte[RunDemands * jokerStates];
        }

        public static Workspace Rent(int jokerStates)
        {
            if (jokerStates > MaxKeptJokerStates) return new Workspace(jokerStates);
            if (_kept is { } kept && kept._jokerStates >= jokerStates) return kept;
            return _kept = new Workspace(jokerStates);
        }

        public static void Discard() => _kept = null;

        /// <summary>Marks the first <paramref name="count"/> current states unreached again.</summary>
        public void Forget(int count)
        {
            for (var i = 0; i < count; i++)
                Objective[States[i]] = long.MinValue;
        }

        public void Advance()
        {
            (Objective, NextObjective) = (NextObjective, Objective);
            (States, NextStates) = (NextStates, States);
        }
    }
}
