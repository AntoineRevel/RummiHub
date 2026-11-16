using RummiHub.Core.Models;

namespace RummiHub.Web.Models;

/// <summary>
/// The table of a game as each screen width sees it: one <see cref="BoardGrid"/> layout per
/// table width in use (a phone fits fewer columns than a desktop), moved forward with every
/// board the game shows. Kept on the game, not on the page, so a reload or a reconnection
/// finds the sets where they were. Thread-safe.
/// </summary>
public sealed class BoardLayouts
{
    public const int MinColumns = 12;
    public const int MaxColumns = 120;

    /// <summary>Layouts kept at most; the ones a connected page uses are never dropped.</summary>
    private const int MaxLayouts = 12;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, BoardSpot[]> _spots = [];
    private readonly List<int> _recent = [];
    private readonly Dictionary<string, int> _users = [];
    private ValidSet[] _board = [];

    public static int Clamp(int columns) => Math.Clamp(columns, MinColumns, MaxColumns);

    /// <summary>Moves every layout forward to <paramref name="board"/>, the board now shown.</summary>
    public void Sync(IReadOnlyList<ValidSet> board)
    {
        lock (_lock)
        {
            if (Same(_board, board)) return;
            foreach (var columns in _spots.Keys.ToList())
                _spots[columns] = BoardGrid.Place(_board, _spots[columns], board, columns);
            _board = [.. board];
        }
    }

    /// <summary>
    /// The page on <paramref name="connectionId"/> shows a table <paramref name="columns"/>
    /// wide: its layout is kept from now on, laid from scratch if no page used that width.
    /// </summary>
    public void Use(string connectionId, int columns)
    {
        columns = Clamp(columns);
        lock (_lock)
        {
            _users[connectionId] = columns;
            _recent.Remove(columns);
            _recent.Add(columns);
            if (!_spots.ContainsKey(columns))
                _spots[columns] = BoardGrid.Place([], [], _board, columns);

            while (_spots.Count > MaxLayouts
                   && _recent.FirstOrDefault(c => !_users.ContainsValue(c), -1) is var idle and >= 0)
            {
                _recent.Remove(idle);
                _spots.Remove(idle);
            }
        }
    }

    /// <summary>The page left; its layout stays for a while, in case it comes back.</summary>
    public void Release(string connectionId)
    {
        lock (_lock) _users.Remove(connectionId);
    }

    /// <summary>Per table width, the spots of the board's sets, in order: row, column, row, column…</summary>
    public Dictionary<int, int[]> Snapshot()
    {
        lock (_lock)
            return _spots.ToDictionary(entry => entry.Key,
                entry => entry.Value.SelectMany(spot => new[] { spot.Row, spot.Col }).ToArray());
    }

    private static bool Same(IReadOnlyList<ValidSet> a, IReadOnlyList<ValidSet> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i]) && !a[i].Tiles.AsSpan().SequenceEqual(b[i].Tiles))
                return false;
        return true;
    }
}
