using System.Numerics;
using RummiHub.Core.Models;

namespace RummiHub.Solver.Worker.Solver.Dynamic;

/// <summary>
/// Finds the way to lay the board's tiles plus the played tiles into valid sets that moves
/// the fewest tiles, exactly. Each previous set can live on in one new set; a tile stays put
/// when it is still in the set its old set lives on in, so a new set is charged its tiles
/// minus those it shares with the previous set it takes over (all of them when it takes over
/// none). A played tile always costs one. Among the arrangements moving that fewest number
/// of tiles, the one changing the fewest sets wins.
///
/// <para>
/// The search always decides the set of the lowest tile left (value first, then color), so
/// what is left to lay is a plain multiset, plus which previous sets are taken over among
/// those that still matter to it. That fully decides what the rest will cost: the cost is
/// remembered, and every other path reaching the same point reuses it, so two parts of the
/// table that do not touch are each worked out once. The allowed cost grows one moved tile
/// at a time from "only the played tiles move": the first arrangement found is the cheapest,
/// and a move that needs few board tiles to change set is answered after a few hundred sets
/// tried, wherever on the board it happens.
/// </para>
/// </summary>
internal sealed class BoardRecomposer
{
    /// <summary>Cost of one moved tile; below it, one unit per set that is not kept as it was.</summary>
    private const int MoveCost = 64;

    /// <summary>No arrangement within the budget.</summary>
    private const int Fail = int.MaxValue;

    /// <summary>No arrangement at all: the tiles left cannot be laid into sets.</summary>
    private const int Dead = int.MaxValue - 1;

    private const int TileKinds = 52;
    private const int JokerShift = TileKinds * 2;

    private static readonly UInt128 JokerUnit = UInt128.One << JokerShift;
    private static readonly UInt128 Reals = JokerUnit - 1;

    private readonly int[] _setSize;
    private readonly int[] _setJokers;

    /// <summary>For each tile kind, the previous sets holding one, as bits; last, those holding a joker.</summary>
    private readonly ulong[] _setsOfKind = new ulong[TileKinds + 1];

    private readonly Dictionary<(UInt128 Left, ulong TakenOver), (int Cost, int FailedBudget, Candidate Choice)>
        _memo = new();

    private readonly HashSet<UInt128> _dead = [];
    private readonly Dictionary<UInt128, (int Home, int Shared)[]> _homesOfSet = new();
    private readonly List<Candidate> _candidates = [];
    private readonly List<(int Home, int Shared)> _hits = [];

    private readonly UInt128 _all;
    private int _workLeft;
    private bool _aborted;
    private bool _keepFound;

    /// <summary>A set for the lowest tile left, taking over <see cref="Home"/> (-1: no set).</summary>
    private readonly record struct Candidate(UInt128 Set, int Home, int Cost);

    private BoardRecomposer(IReadOnlyList<ValidSet> previous, IReadOnlyList<Tile> played, int maxWork)
    {
        _setSize = new int[previous.Count];
        _setJokers = new int[previous.Count];
        for (var i = 0; i < previous.Count; i++)
        {
            _setSize[i] = previous[i].Tiles.Length;
            foreach (var tile in previous[i].Tiles)
            {
                _all += Unit(tile);
                _setsOfKind[tile.IsJoker ? TileKinds : Kind(tile)] |= 1UL << i;
                if (tile.IsJoker) _setJokers[i]++;
            }
        }

        foreach (var tile in played) _all += Unit(tile);
        _workLeft = maxWork;
    }

    /// <summary>
    /// The sets of the arrangement moving the fewest tiles, each as its tiles (jokers bare).
    /// When <paramref name="maxWork"/> (sets tried) runs out first, the best arrangement
    /// found within <paramref name="lastPassWork"/> more, with <paramref name="cheapest"/>
    /// false, or null.
    /// </summary>
    public static List<List<Tile>>? Recompose(IReadOnlyList<ValidSet> previous, IReadOnlyList<Tile> played,
        int maxWork, int lastPassWork, out bool cheapest, CancellationToken cancellationToken = default)
    {
        cheapest = false;

        // One bit per previous set, two bits per tile kind: neither a 65th set nor a fourth
        // copy of a tile (never dealt) would fit.
        if (previous.Count > 64) return null;
        var total = new int[TileKinds + 1];
        foreach (var tile in previous.SelectMany(s => s.Tiles).Concat(played))
            if (++total[tile.IsJoker ? TileKinds : Kind(tile)] > 3)
                return null;

        var search = new BoardRecomposer(previous, played, maxWork);
        var boardTiles = previous.Sum(s => s.Tiles.Length);
        for (var movedOnBoard = 0; movedOnBoard <= boardTiles; movedOnBoard++)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            var budget = (played.Count + movedOnBoard) * MoveCost + MoveCost - 1;
            var cost = search.Solve(search._all, 0, budget);
            if (cost == Dead) return null;
            if (search._aborted) break;
            if (cost == Fail) continue;
            cheapest = true;
            return search.Sets();
        }

        // Too far from the table to find the cheapest arrangement: settle for the best one a
        // last pass comes across, cheapest sets first, with no limit on the cost.
        (search._aborted, search._keepFound, search._workLeft) = (false, true, lastPassWork);
        var found = search.Solve(search._all, 0, (played.Count + boardTiles) * MoveCost);
        return found is Fail or Dead ? null : search.Sets();
    }

    /// <summary>
    /// Cheapest way to lay <paramref name="left"/> into sets when it costs at most
    /// <paramref name="budget"/>, else <see cref="Fail"/> or <see cref="Dead"/>.
    /// <paramref name="takenOver"/> has one bit per previous set a set laid earlier took over.
    /// </summary>
    private int Solve(UInt128 left, ulong takenOver, int budget)
    {
        if (left == UInt128.Zero) return 0;

        var lowest = (int)UInt128.TrailingZeroCount(left) / 2;
        if (lowest >= TileKinds || _dead.Contains(left)) return Dead; // only jokers left

        // A previous set none of whose tiles is left can no longer be taken over: forgetting
        // it lets every path that reaches this point share one answer.
        var inReach = InReach(left);
        takenOver &= inReach;
        if (LeastCost(left, inReach & ~takenOver) > budget) return Fail;

        if (_memo.TryGetValue((left, takenOver), out var known))
        {
            if (known.Cost != Fail) return known.Cost <= budget ? known.Cost : Fail;
            if (known.FailedBudget >= budget) return Fail;
        }

        var start = _candidates.Count;
        AddSetsOf(left, lowest, takenOver);
        var count = _candidates.Count - start;
        _candidates.Sort(start, count, CheapestFirst);
        if ((_workLeft -= count + 1) < 0) _aborted = true;

        var best = Fail;
        var choice = default(Candidate);
        var dead = true;
        for (var i = start; i < start + count && !_aborted; i++)
        {
            var candidate = _candidates[i];
            var limit = (best == Fail ? budget : best - 1) - candidate.Cost;
            if (limit < 0)
            {
                dead = false;
                break;
            }

            var rest = Solve(left - candidate.Set,
                candidate.Home < 0 ? takenOver : takenOver | (1UL << candidate.Home), limit);
            if (rest != Dead) dead = false;
            if (rest is Fail or Dead) continue;
            best = candidate.Cost + rest;
            choice = candidate;
        }

        _candidates.RemoveRange(start, count);

        // Out of work: nothing is proven any more, but in the last pass an arrangement found
        // so far is still an arrangement.
        if (_aborted && (best == Fail || !_keepFound)) return Fail;

        // Whether the tiles can be laid at all only depends on the tiles.
        if (dead)
        {
            _dead.Add(left);
            return Dead;
        }

        _memo[(left, takenOver)] = best != Fail ? (best, 0, choice) : (Fail, budget, default);
        return best;
    }

    private static readonly Comparer<Candidate> CheapestFirst =
        Comparer<Candidate>.Create((a, b) => a.Cost.CompareTo(b.Cost));

    /// <summary>The previous sets a set made of <paramref name="left"/> could still take over.</summary>
    private ulong InReach(UInt128 left)
    {
        var sets = 0UL;
        for (var kinds = (ulong)left; kinds != 0; kinds &= kinds - 1)
            sets |= _setsOfKind[BitOperations.TrailingZeroCount(kinds) / 2];
        for (var kinds = (ulong)(left >> 64) & ((1UL << (JokerShift - 64)) - 1); kinds != 0; kinds &= kinds - 1)
            sets |= _setsOfKind[32 + BitOperations.TrailingZeroCount(kinds) / 2];
        return sets;
    }

    /// <summary>
    /// The least <paramref name="left"/> can cost: a tile only stays put in a set taking over
    /// a previous set that held one like it, so each one beyond the sets still
    /// <paramref name="vacant"/> moves. The played tiles are the first of those.
    /// </summary>
    private int LeastCost(UInt128 left, ulong vacant)
    {
        var moved = 0;
        for (var kinds = (ulong)left; kinds != 0;)
        {
            var bit = BitOperations.TrailingZeroCount(kinds) & ~1;
            moved += Math.Max(0, (int)(kinds >> bit & 3) - BitOperations.PopCount(vacant & _setsOfKind[bit / 2]));
            kinds &= ~(3UL << bit);
        }

        for (var kinds = (ulong)(left >> 64) & ((1UL << (JokerShift - 64)) - 1); kinds != 0;)
        {
            var bit = BitOperations.TrailingZeroCount(kinds) & ~1;
            moved += Math.Max(0, (int)(kinds >> bit & 3) - BitOperations.PopCount(vacant & _setsOfKind[32 + bit / 2]));
            kinds &= ~(3UL << bit);
        }

        var jokers = Count(left, JokerShift);
        for (var sets = vacant & _setsOfKind[TileKinds]; sets != 0 && jokers > 0; sets &= sets - 1)
            jokers -= _setJokers[BitOperations.TrailingZeroCount(sets)];
        return (moved + Math.Max(0, jokers)) * MoveCost;
    }

    /// <summary>Every valid set of <paramref name="left"/> whose lowest tile is <paramref name="lowest"/>.</summary>
    private void AddSetsOf(UInt128 left, int lowest, ulong takenOver)
    {
        var jokers = Count(left, JokerShift);
        var value = lowest / 4 + 1;
        var color = lowest % 4;
        var first = UInt128.One << (lowest * 2);

        AddRuns(left, takenOver, value, color, jokers, high: value, set: first, gaps: 0);

        // Colors below this one have no tile of the value left: it is the lowest tile.
        var others = 0;
        for (var c = color + 1; c < 4; c++)
            if (Count(left, (lowest - color + c) * 2) > 0)
                others |= 1 << c;

        for (var subset = others;; subset = (subset - 1) & others)
        {
            var set = first;
            var reals = 1;
            for (var c = color + 1; c < 4; c++)
            {
                if ((subset & (1 << c)) == 0) continue;
                set += UInt128.One << ((lowest - color + c) * 2);
                reals++;
            }

            for (var spare = Math.Max(0, 3 - reals); spare <= jokers && reals + spare <= 4; spare++)
                Add(set + JokerUnit * (uint)spare, takenOver);

            if (subset == 0) break;
        }
    }

    /// <summary>
    /// Runs starting at <paramref name="value"/> and reaching <paramref name="high"/> so far,
    /// <paramref name="gaps"/> jokers standing inside. Spare jokers go to either end.
    /// </summary>
    private void AddRuns(UInt128 left, ulong takenOver, int value, int color, int jokers, int high,
        UInt128 set, int gaps)
    {
        var room = value - 1 + 13 - high;
        for (var spare = 0; spare <= jokers - gaps && spare <= room; spare++)
            if (high - value + 1 + spare >= 3)
                Add(set + JokerUnit * (uint)(gaps + spare), takenOver);

        for (var gap = 0; gap <= jokers - gaps; gap++)
        {
            var next = high + 1 + gap;
            if (next > 13) break;
            var shift = ((next - 1) * 4 + color) * 2;
            if (Count(left, shift) > 0)
                AddRuns(left, takenOver, value, color, jokers, next, set + (UInt128.One << shift), gaps + gap);
        }
    }

    /// <summary>
    /// The ways the set can be laid: taking over any previous set it shares a tile with that
    /// is not taken over yet (the tiles they share stay put), or none. One unit more when it
    /// is not a previous set kept whole.
    /// </summary>
    private void Add(UInt128 set, ulong takenOver)
    {
        var size = Size(set);
        var homes = HomesOf(set);
        var whole = false;
        foreach (var (home, shared) in homes)
            whole |= shared == _setSize[home] && (takenOver & (1UL << home)) == 0;

        // A previous set this one contains whole keeps all its tiles here: no other set
        // could keep more of it, so nothing else is worth trying.
        foreach (var (home, shared) in homes)
        {
            if ((takenOver & (1UL << home)) != 0 || (whole && shared != _setSize[home])) continue;
            var kept = shared == size && size == _setSize[home];
            _candidates.Add(new Candidate(set, home, (size - shared) * MoveCost + (kept ? 0 : 1)));
        }

        if (!whole) _candidates.Add(new Candidate(set, -1, size * MoveCost + 1));
    }

    /// <summary>The previous sets sharing a real tile with the set, with how many tiles they share.</summary>
    private (int Home, int Shared)[] HomesOf(UInt128 set)
    {
        if (_homesOfSet.TryGetValue(set, out var homes)) return homes;

        _hits.Clear();
        for (var kinds = set & Reals; kinds != UInt128.Zero; kinds &= kinds - 1)
        {
            for (var sets = _setsOfKind[(int)UInt128.TrailingZeroCount(kinds) / 2]; sets != 0; sets &= sets - 1)
            {
                var index = BitOperations.TrailingZeroCount(sets);
                var slot = _hits.FindIndex(h => h.Home == index);
                if (slot < 0) _hits.Add((index, 1));
                else _hits[slot] = (index, _hits[slot].Shared + 1);
            }
        }

        var jokers = Count(set, JokerShift);
        homes = new (int Home, int Shared)[_hits.Count];
        for (var i = 0; i < homes.Length; i++)
            homes[i] = (_hits[i].Home, _hits[i].Shared + Math.Min(jokers, _setJokers[_hits[i].Home]));
        return _homesOfSet[set] = homes;
    }

    /// <summary>The arrangement the last successful <see cref="Solve"/> settled on.</summary>
    private List<List<Tile>> Sets()
    {
        var sets = new List<List<Tile>>();
        var takenOver = 0UL;
        for (var left = _all; left != UInt128.Zero;)
        {
            takenOver &= InReach(left);
            var (set, home, _) = _memo[(left, takenOver)].Choice;
            if (home >= 0) takenOver |= 1UL << home;

            var tiles = new List<Tile>();
            for (var kind = 0; kind < TileKinds; kind++)
                if (Count(set, kind * 2) > 0)
                    tiles.Add(new Tile(kind / 4 + 1, (TileColor)(kind % 4)));
            for (var i = Count(set, JokerShift); i > 0; i--)
                tiles.Add(new Tile(isJoker: true));

            sets.Add(tiles);
            left -= set;
        }

        return sets;
    }

    private static int Size(UInt128 set)
    {
        var size = Count(set, JokerShift);
        for (var kinds = set & Reals; kinds != UInt128.Zero; kinds &= kinds - 1) size++;
        return size;
    }

    private static int Kind(Tile tile) => (tile.Value - 1) * 4 + (int)tile.Color;

    private static UInt128 Unit(Tile tile) => tile.IsJoker ? JokerUnit : UInt128.One << (Kind(tile) * 2);

    private static int Count(UInt128 tiles, int shift) => (int)(uint)(tiles >> shift) & 3;
}
