using RummiHub.Core.Models;

namespace RummiHub.Core.Contracts;

/// <summary>
/// Rejects solve payloads that cannot occur in a real game. The dynamic-programming
/// solver's state grows with the joker budget and with illegal duplicates, so those
/// bounds are checked before a request is queued.
/// </summary>
public static class SolveRequestValidator
{
    public const int MaxTiles = 106;
    public const int MaxJokers = 2;
    public const int MaxCopiesPerTile = 2;
    public const int MaxSets = 40;
    public const int MaxTilesPerSet = 13;
    public const int MaxRequestIdLength = 64;
    public const int ContentHashLength = 16;

    public static bool TryValidate(SolveRequest? request, out string error)
    {
        if (request is null)
            return Fail("Solve request is required.", out error);

        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > MaxRequestIdLength)
            return Fail($"RequestId must be 1 to {MaxRequestIdLength} characters.", out error);

        if (request.Hand is null)
            return Fail("Hand is required.", out error);

        if (request.Board?.Sets is null)
            return Fail("Board is required.", out error);

        if (request.Hand.Count > MaxTiles)
            return Fail($"Hand cannot exceed {MaxTiles} tiles.", out error);

        if (request.Board.Sets.Count > MaxSets)
            return Fail($"Board cannot exceed {MaxSets} sets.", out error);

        var boardTiles = new List<Tile>();
        if (!TryCollect(request.Board.Sets, boardTiles, out error))
            return false;

        if (boardTiles.Count > MaxTiles)
            return Fail($"Board cannot exceed {MaxTiles} tiles.", out error);

        if (request.Hand.Count + boardTiles.Count > MaxTiles)
            return Fail($"Hand and board together cannot exceed {MaxTiles} tiles.", out error);

        return TryValidateTiles(request.Hand, boardTiles, out error);
    }

    /// <summary>True for the 16-char lowercase hex id produced by <see cref="ContentHasher"/>.</summary>
    public static bool IsContentHash(string? hash) =>
        hash is { Length: ContentHashLength }
        && hash.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryCollect(List<ValidSet> sets, List<Tile> into, out string error)
    {
        foreach (var set in sets)
        {
            if (set?.Tiles is null)
                return Fail("Board contains an empty set.", out error);

            if (set.Tiles.Length > MaxTilesPerSet)
                return Fail($"A set cannot exceed {MaxTilesPerSet} tiles.", out error);

            into.AddRange(set.Tiles);
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateTiles(List<Tile> hand, List<Tile> board, out string error)
    {
        var jokers = 0;
        var copies = new Dictionary<(int Value, TileColor Color), int>();

        foreach (var tile in hand.Concat(board))
        {
            if (tile.IsJoker)
            {
                jokers++;
                if (jokers > MaxJokers)
                    return Fail($"A game has at most {MaxJokers} jokers.", out error);
                continue;
            }

            if (!Enum.IsDefined(tile.Color) || tile.Value is < 1 or > 13)
                return Fail("Tiles must be values 1 to 13 in a known color, or jokers.", out error);

            var key = (tile.Value, tile.Color);
            copies.TryGetValue(key, out var count);
            count++;
            if (count > MaxCopiesPerTile)
                return Fail($"Each tile may appear at most {MaxCopiesPerTile} times.", out error);

            copies[key] = count;
        }

        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
