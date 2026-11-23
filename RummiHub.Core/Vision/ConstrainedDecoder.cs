using RummiHub.Core.Models;

namespace RummiHub.Core.Vision;

/// <summary>
/// What the tile reader saw on one tile: a probability for each face and each color.
/// <see cref="Faces"/> has 14 entries, values 1 to 13 then the joker; <see cref="Colors"/> has
/// one per <see cref="TileColor"/>, in enum order (ignored for a joker).
/// </summary>
public sealed record TileReading(float[] Faces, float[] Colors)
{
    public const int JokerFace = 13;

    public static TileReading Certain(Tile tile)
    {
        var faces = new float[14];
        var colors = new float[4];
        faces[tile.IsJoker ? JokerFace : tile.Value - 1] = 1;
        colors[(int)tile.Color] = 1;
        return new TileReading(faces, colors);
    }

    /// <summary>The reader's own first choice, rules ignored.</summary>
    public Tile Best()
    {
        var face = ArgMax(Faces);
        return face == JokerFace ? new Tile(isJoker: true) : new Tile(face + 1, (TileColor)ArgMax(Colors));
    }

    private static int ArgMax(float[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }
}

/// <summary>One tile as finally read: <see cref="Corrected"/> when the rules overruled the reader's first choice.</summary>
public sealed record ReadTile(Tile Tile, bool Corrected, double Confidence);

/// <summary>One set of the table. Not <see cref="Legal"/> when no legal set fits it: the player has to fix it.</summary>
public sealed record ReadSet(IReadOnlyList<ReadTile> Tiles, bool Legal);

/// <summary>
/// A whole photographed game. <see cref="WithinTileCounts"/> is false when no reading keeps every
/// tile to its two copies and the jokers to two (the photo needs fixing by hand).
/// </summary>
public sealed record ReadGame(IReadOnlyList<ReadSet> Table, IReadOnlyList<ReadTile> Rack, bool WithinTileCounts)
{
    public bool NeedsReview => !WithinTileCounts || Table.Any(s => !s.Legal);

    public Solution ToBoard() => new()
    {
        Sets = Table.Select(s => new ValidSet(s.Tiles.Select(t => t.Tile).ToArray())).ToList(),
        IsValid = true,
    };

    public List<Tile> ToHand() => Rack.Select(t => t.Tile).ToList();
}

/// <summary>
/// Picks the most probable reading of a photographed game that obeys the rules of the game:
/// every set on the table is a legal run or group, each tile exists twice at most, and there are
/// two jokers at most. The reader's errors rarely survive this (a 6 read in a run 4-5-?-7, a
/// second red in a group), and a joker on the table gets the tile it stands for, which the
/// solver needs. The search is exact (best-first over each set's best legal readings).
/// </summary>
public static class ConstrainedDecoder
{
    /// <summary>Legal readings kept per set, and readings per loose tile (rack, unreadable set).</summary>
    public const int CandidatesPerItem = 24;

    /// <summary>Partial readings explored before giving up on the tile counts.</summary>
    public const int MaxExpansions = 200_000;

    /// <summary>
    /// The rules may overrule the reader only where it hesitated: a reading less likely than
    /// this, next to the reader's own first choice, is never considered. Without it, a tile read
    /// for sure would silently turn into anything to make the counts fit.
    /// </summary>
    public const double MinOddsAgainstReader = 1e-3;

    private const int MaxCopies = 2;
    private const int MaxJokers = 2;
    private const double Floor = 1e-6;

    public static ReadGame Decode(IReadOnlyList<IReadOnlyList<TileReading>> table, IReadOnlyList<TileReading> rack)
    {
        // Every set (or loose tile) becomes an item with its candidate readings, best first.
        var items = new List<Item>();
        var setItems = new List<(int First, int Count, bool Legal)>();
        foreach (var set in table)
        {
            var candidates = SetCandidates(set);
            if (candidates.Count > 0)
            {
                setItems.Add((items.Count, 1, true));
                items.Add(new Item(set, candidates));
            }
            else
            {
                // No legal set fits these tiles (two sets touching, a tile missed by the
                // detector...): each tile is read on its own and the player fixes the set.
                setItems.Add((items.Count, set.Count, false));
                items.AddRange(set.Select(t => new Item([t], TileCandidates(t))));
            }
        }

        var rackStart = items.Count;
        items.AddRange(rack.Select(t => new Item([t], TileCandidates(t))));

        var choice = BestWithinCounts(items);
        var withinCounts = choice is not null;
        choice ??= new int[items.Count];

        var readSets = setItems.Select(s => new ReadSet(
            Enumerable.Range(s.First, s.Count).SelectMany(i => Read(items[i], choice[i])).ToList(),
            s.Legal)).ToList();
        var readRack = Enumerable.Range(rackStart, rack.Count).SelectMany(i => Read(items[i], choice[i])).ToList();

        return new ReadGame(readSets, readRack, withinCounts);
    }

    private sealed record Candidate(Tile[] Tiles, double LogProb);

    private sealed record Item(IReadOnlyList<TileReading> Readings, List<Candidate> Candidates);

    private static IEnumerable<ReadTile> Read(Item item, int candidate)
    {
        var tiles = item.Candidates[candidate].Tiles;
        for (var i = 0; i < tiles.Length; i++)
        {
            var reading = item.Readings[i];
            var best = reading.Best();
            var tile = tiles[i];
            var corrected = tile.IsJoker != best.IsJoker
                            || (!tile.IsJoker && (tile.Value != best.Value || tile.Color != best.Color));
            yield return new ReadTile(tile, corrected, Math.Exp(LogProb(reading, tile)));
        }
    }

    private static double Log(float p) => Math.Log(Math.Max(p, Floor));

    private static double LogProb(TileReading r, Tile tile) => tile.IsJoker
        ? Log(r.Faces[TileReading.JokerFace])
        : Log(r.Faces[tile.Value - 1]) + Log(r.Colors[(int)tile.Color]);

    private static List<Candidate> TileCandidates(TileReading reading)
    {
        var all = new List<Candidate> { new([new Tile(isJoker: true)], Log(reading.Faces[TileReading.JokerFace])) };
        foreach (var color in Enum.GetValues<TileColor>())
            for (var value = 1; value <= 13; value++)
            {
                var tile = new Tile(value, color);
                all.Add(new Candidate([tile], LogProb(reading, tile)));
            }

        return Plausible(all, all.Max(c => c.LogProb));
    }

    /// <summary>Every legal run and group these tiles could be, in the order they lie, best first.</summary>
    private static List<Candidate> SetCandidates(IReadOnlyList<TileReading> set)
    {
        var n = set.Count;
        var all = new List<Candidate>();
        if (n < 3) return all;

        var jokerMasks = JokerMasks(n);

        // Runs: one color, consecutive values along the line, either way (a photo taken from
        // the other side of the table reads the run backwards). A joker stands for its seat.
        if (n <= 13)
            foreach (var color in Enum.GetValues<TileColor>())
                for (var start = 1; start + n - 1 <= 13; start++)
                    foreach (var ascending in new[] { true, false })
                    {
                        var faces = new Tile[n];
                        for (var i = 0; i < n; i++)
                            faces[i] = new Tile(ascending ? start + i : start + n - 1 - i, color);
                        foreach (var mask in jokerMasks)
                            all.Add(WithJokers(set, faces, mask));
                    }

        // Groups: one value, three or four different colors; a joker takes a color left free.
        if (n is 3 or 4)
            for (var value = 1; value <= 13; value++)
                foreach (var mask in jokerMasks)
                {
                    var realSeats = Enumerable.Range(0, n).Where(i => (mask & (1 << i)) == 0).ToArray();
                    foreach (var colors in Arrangements(Enum.GetValues<TileColor>(), realSeats.Length))
                    {
                        var faces = new Tile[n];
                        for (var k = 0; k < realSeats.Length; k++)
                            faces[realSeats[k]] = new Tile(value, colors[k]);
                        var free = Enum.GetValues<TileColor>().Except(colors).ToArray();
                        var next = 0;
                        for (var i = 0; i < n; i++)
                            if ((mask & (1 << i)) != 0)
                                faces[i] = new Tile(value, free[next++]);
                        all.Add(WithJokers(set, faces, mask));
                    }
                }

        return Plausible(all, set.Sum(r => LogProb(r, r.Best())));
    }

    private static Candidate WithJokers(IReadOnlyList<TileReading> set, Tile[] faces, int mask)
    {
        var tiles = new Tile[faces.Length];
        double logProb = 0;
        for (var i = 0; i < faces.Length; i++)
        {
            tiles[i] = (mask & (1 << i)) != 0
                ? new Tile(faces[i].Value, faces[i].Color, isJoker: true)
                : faces[i];
            logProb += LogProb(set[i], tiles[i]);
        }

        return new Candidate(tiles, logProb);
    }

    /// <summary>Every way to put at most two jokers among n seats, as bit masks.</summary>
    private static List<int> JokerMasks(int n)
    {
        var masks = new List<int> { 0 };
        for (var i = 0; i < n; i++)
        {
            masks.Add(1 << i);
            for (var j = i + 1; j < n; j++)
                masks.Add((1 << i) | (1 << j));
        }

        // A set needs at least one real tile to say what it is (three seats, two jokers: fine).
        return masks.Where(m => System.Numerics.BitOperations.PopCount((uint)m) < n).ToList();
    }

    private static IEnumerable<TileColor[]> Arrangements(TileColor[] colors, int count)
    {
        if (count == 0)
        {
            yield return [];
            yield break;
        }

        foreach (var first in colors)
            foreach (var rest in Arrangements(colors.Where(c => c != first).ToArray(), count - 1))
                yield return [first, .. rest];
    }

    /// <summary>The best candidates no less likely than <see cref="MinOddsAgainstReader"/> next to <paramref name="readerBest"/>.</summary>
    private static List<Candidate> Plausible(List<Candidate> all, double readerBest) => all
        .Where(c => c.LogProb >= readerBest + Math.Log(MinOddsAgainstReader))
        .OrderByDescending(c => c.LogProb)
        .Take(CandidatesPerItem)
        .ToList();

    private sealed record Node(int Depth, int[] Choice, byte[] Copies, int Jokers, double LogProb);

    /// <summary>
    /// Best-first search for the most probable choice of one candidate per item that keeps the
    /// tile counts. Its bound (best candidate of every item left) never underestimates, so the
    /// first complete reading taken off the queue is the best one. Null when none is found.
    /// </summary>
    private static int[]? BestWithinCounts(List<Item> items)
    {
        // rest[d] = best log-probability the items from d on could still add.
        var rest = new double[items.Count + 1];
        for (var d = items.Count - 1; d >= 0; d--)
            rest[d] = rest[d + 1] + (items[d].Candidates.Count > 0 ? items[d].Candidates[0].LogProb : 0);

        var queue = new PriorityQueue<Node, double>();
        queue.Enqueue(new Node(0, [], new byte[64], 0, 0), -rest[0]);

        for (var expansions = 0; queue.Count > 0 && expansions < MaxExpansions; expansions++)
        {
            var node = queue.Dequeue();
            if (node.Depth == items.Count) return node.Choice;

            var candidates = items[node.Depth].Candidates;
            for (var c = 0; c < candidates.Count; c++)
            {
                var copies = (byte[])node.Copies.Clone();
                var jokers = node.Jokers;
                var fits = true;
                foreach (var tile in candidates[c].Tiles)
                {
                    if (tile.IsJoker)
                    {
                        fits = ++jokers <= MaxJokers;
                    }
                    else
                    {
                        var key = (int)tile.Color << 4 | tile.Value;
                        fits = ++copies[key] <= MaxCopies;
                    }

                    if (!fits) break;
                }

                if (!fits) continue;

                var logProb = node.LogProb + candidates[c].LogProb;
                queue.Enqueue(new Node(node.Depth + 1, [.. node.Choice, c], copies, jokers, logProb),
                    -(logProb + rest[node.Depth + 1]));
            }
        }

        return null;
    }
}
