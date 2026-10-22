namespace RummiHub.Core.Models;

/// <summary>
/// Represents a collection of tiles with explicit joker handling.
/// The Tiles list NEVER contains jokers — jokers are tracked separately via <see cref="Jokers"/>.
/// </summary>
public class Set
{
    public Set()
    {
        Tiles = [];
        Jokers = 0;
    }

    public Set(List<Tile> tiles)
    {
        Tiles = [..tiles];
        NormalizeAfterMutation();
    }

    public Set(Set set)
    {
        Tiles = [..set.Tiles];
        Jokers = set.Jokers;
    }

    /// <summary>
    /// Never contains jokers. Use <see cref="Jokers"/> for joker count.
    /// </summary>
    public List<Tile> Tiles { get; }

    public int Jokers { get; private set; }

    public IEnumerable<Tile> GetAllTilesIncludingJokers()
    {
        foreach (var tile in Tiles)
            yield return tile;

        for (var i = 0; i < Jokers; i++)
            yield return new Tile(0, isJoker: true);
    }

    public void AddTile(Tile tile)
    {
        if (tile.IsJoker)
            Jokers++;
        else
            Tiles.Add(tile);
    }

    public void Concat(ValidSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        foreach (var tile in set.Tiles)
            if (tile.IsJoker)
                Jokers++;
            else
                Tiles.Add(tile);
    }

    /// <summary>
    /// Removes one occurrence of <paramref name="tile"/>.
    /// Returns <c>false</c> and leaves the set unchanged when that tile is absent
    /// (a joker is absent when <see cref="Jokers"/> is already zero).
    /// </summary>
    public bool Remove(Tile tile)
    {
        if (tile.IsJoker)
        {
            if (Jokers <= 0)
                return false;

            Jokers--;
            return true;
        }

        return Tiles.Remove(tile);
    }

    /// <summary>
    /// True when <paramref name="tiles"/> is a sub-multiset of this set.
    /// Duplicate copies are counted. Every joker — whatever value or colour it
    /// still carries — consumes one entry of <see cref="Jokers"/>, never a numbered tile.
    /// </summary>
    public bool ContainsAll(IEnumerable<Tile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var neededJokers = 0;
        var needed = new List<Tile>();
        foreach (var tile in tiles)
        {
            if (tile.IsJoker)
                neededJokers++;
            else
                needed.Add(tile);
        }

        if (neededJokers > Jokers)
            return false;

        var available = new List<Tile>(Tiles);
        foreach (var tile in needed)
        {
            if (!available.Remove(tile))
                return false;
        }

        return true;
    }

    private void NormalizeAfterMutation()
    {
        var jokerCount = Tiles.Count(t => t.IsJoker);
        if (jokerCount <= 0) return;

        Tiles.RemoveAll(t => t.IsJoker);
        Jokers += jokerCount;
    }
}
