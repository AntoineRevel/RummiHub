using System.Net.Http.Json;
using System.Security.Cryptography;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

/// <summary>
/// Local game engine that manages game state (tile pool, players, board, turns)
/// and delegates solving to the distributed solver via the API.
/// </summary>
public sealed class GameEngine
{
    private readonly Queue<Tile> _tilePool = new();

    public Guid Id { get; private set; }
    public List<GamePlayer> Players { get; } = [];
    public int Turn { get; private set; }
    public int PlayerIndex { get; private set; }
    public Solution Board { get; set; } = new();
    public bool IsGameOver { get; private set; }

    public GamePlayer CurrentPlayer => Players[PlayerIndex];
    public int TilePoolCount => _tilePool.Count;
    public string? WinnerName { get; private set; }

    public void Initialize(
        List<string> playerNames,
        string? distributionSeed = null,
        IReadOnlyList<bool>? autoPlayers = null)
    {
        ArgumentNullException.ThrowIfNull(playerNames);
        if (playerNames.Count is < 1 or > GameLimits.MaxPlayers)
            throw new ArgumentOutOfRangeException(nameof(playerNames),
                $"Expected between 1 and {GameLimits.MaxPlayers} players.");

        Id = Guid.NewGuid();
        Turn = 0;
        PlayerIndex = 0;
        IsGameOver = false;
        Board = new Solution();
        Players.Clear();
        _tilePool.Clear();

        var tiles = GenerateTiles();
        // A caller-supplied seed stays deterministic so configured replays keep their deal.
        // The seed itself is chosen by the server (or taken from the server list). With no
        // seed, the shuffle draws from a CSPRNG rather than System.Random.
        if (distributionSeed is not null)
            Shuffle(tiles, new Random(GetStableHash(distributionSeed)));
        else
            ShuffleSecure(tiles);
        DistributeTiles(tiles, playerNames, autoPlayers);
    }

    /// <summary>
    /// Sends a solve request to the API for the current player's rack.
    /// Returns the SolveResponse from the worker.
    /// </summary>
    public async Task<SolveResponse> SolveForCurrentPlayer(
        HttpClient httpClient,
        SolveResultSubscriber resultSubscriber,
        CancellationToken ct)
    {
        return await SubmitAndWaitForResult(httpClient, resultSubscriber, BuildSolveRequest(), ct);
    }

    /// <summary>
    /// Builds the solve request for the current player's full rack (the background
    /// optimal-play hint). Not prioritized — it must not jump ahead of move validations.
    /// </summary>
    public SolveRequest BuildSolveRequest()
    {
        var player = CurrentPlayer;
        return new SolveRequest
        {
            RequestId = $"game-{Id.ToString()[..8]}-t{Turn}-p{PlayerIndex}",
            Hand = player.Rack.GetAllTilesIncludingJokers().ToList(),
            Board = Board,
            HasPlayed = player.HasPlayed,
            Priority = false
        };
    }

    /// <summary>
    /// Told to the caller when a play names a tile, an extra copy, or a joker
    /// the current player does not hold.
    /// </summary>
    public const string UnownedSelectionMessage = "You can only play tiles from your rack.";

    /// <summary>
    /// A human selection may be sent to the solver only when it is a sub-multiset of
    /// the current player's rack. Returns <see cref="UnownedSelectionMessage"/> otherwise.
    /// Does not mutate game state and does not call the solver.
    /// </summary>
    public string? SelectionOwnershipError(IReadOnlyList<Tile>? selectedTiles)
    {
        if (selectedTiles is not null && CurrentPlayer.Rack.ContainsAll(selectedTiles))
            return null;

        return UnownedSelectionMessage;
    }

    /// <summary>
    /// Validates a human player's tile selection by sending it through the solver.
    /// Returns the SolveResponse. Caller checks that all selected tiles were used.
    /// A selection that is not a sub-multiset of the current rack is rejected here,
    /// before any solve request is sent.
    /// </summary>
    public async Task<SolveResponse> ValidateHumanPlay(
        List<Tile> selectedTiles,
        HttpClient httpClient,
        SolveResultSubscriber resultSubscriber,
        CancellationToken ct)
    {
        var ownershipError = SelectionOwnershipError(selectedTiles);
        if (ownershipError is not null)
        {
            return new SolveResponse
            {
                RequestId = "unowned-selection",
                Found = false,
                Error = ownershipError
            };
        }

        return await SubmitAndWaitForResult(
            httpClient, resultSubscriber, BuildValidateRequest(selectedTiles), ct);
    }

    /// <summary>
    /// Builds the solve request validating a human's actual move (only the selected tiles).
    /// Marked <c>Priority = true</c> so it is routed to the priority stream and never queues
    /// behind the slow full-rack hint solve.
    /// </summary>
    public SolveRequest BuildValidateRequest(List<Tile> selectedTiles)
    {
        return new SolveRequest
        {
            RequestId = $"validate-{Guid.NewGuid().ToString()[..8]}",
            Hand = selectedTiles,
            Board = Board,
            HasPlayed = CurrentPlayer.HasPlayed,
            Priority = true
        };
    }

    /// <summary>
    /// Applies a successful solve result: updates board, removes played tiles from rack, checks win.
    /// A result that plays tiles the current player does not hold is refused and leaves
    /// the rack, the board, and <see cref="GamePlayer.HasPlayed"/> unchanged.
    /// </summary>
    public void ApplySolveResult(SolveResponse result)
    {
        if (!result.Found || result.NewBoard == null) return;

        var player = CurrentPlayer;
        var played = TilesPlayed(result);

        // Refuse the move before any change when the rack cannot lose every played tile;
        // once it can, each removal below is guaranteed to succeed.
        if (!player.Rack.ContainsAll(played))
            throw new InvalidOperationException(
                "Cannot apply a move that plays tiles absent from the current player's rack.");

        foreach (var tile in played)
            player.Rack.Remove(tile);

        // Track tiles to play for UI highlighting
        player.TilesToPlay = [..result.TilesToPlay];
        for (var i = 0; i < result.JokersToPlay; i++)
            player.TilesToPlay.Add(new Tile(isJoker: true));

        // For first play, merge with existing board
        if (!player.HasPlayed)
        {
            player.HasPlayed = true;
            Board = result.NewBoard.AddSolution(Board);
        }
        else
        {
            Board = result.NewBoard;
        }

        // Check win
        if (player.Rack.Tiles.Count == 0 && player.Rack.Jokers == 0)
        {
            IsGameOver = true;
            WinnerName = player.Name;
        }
    }

    /// <summary>
    /// Draws a tile from the pool and adds it to the current player's rack.
    /// Returns the drawn tile.
    /// </summary>
    public Tile DrawTile()
    {
        var tile = _tilePool.Dequeue();
        CurrentPlayer.Rack.AddTile(tile);
        return tile;
    }

    public void AdvanceToNextPlayer()
    {
        PlayerIndex = (PlayerIndex + 1) % Players.Count;
        if (PlayerIndex == 0) Turn++;
    }

    /// <summary>
    /// Tiles <see cref="ApplySolveResult"/> must take off the rack: the reported hand
    /// tiles, plus one joker per <see cref="SolveResponse.JokersToPlay"/>.
    /// </summary>
    private static List<Tile> TilesPlayed(SolveResponse result)
    {
        var jokersToPlay = Math.Max(result.JokersToPlay, 0);
        var played = new List<Tile>(result.TilesToPlay.Count + jokersToPlay);
        played.AddRange(result.TilesToPlay);
        for (var i = 0; i < jokersToPlay; i++)
            played.Add(new Tile(isJoker: true));
        return played;
    }

    private static async Task<SolveResponse> SubmitAndWaitForResult(
        HttpClient httpClient,
        SolveResultSubscriber resultSubscriber,
        SolveRequest request,
        CancellationToken ct)
    {
        var postResponse = await httpClient.PostAsJsonAsync("/solve", request, ct);
        postResponse.EnsureSuccessStatusCode();

        var accepted = await postResponse.Content.ReadFromJsonAsync<SolveAcceptedDto>(ct);
        if (accepted?.ContentHash is null)
            throw new InvalidOperationException("No content hash received from API");

        return await resultSubscriber.WaitForResult(accepted.ContentHash, ct);
    }

    private static int GetStableHash(string s)
    {
        var hash = 0;
        foreach (var c in s)
            hash = hash * 31 + c;
        return hash;
    }

    private static Tile[] GenerateTiles()
    {
        var tiles = new Tile[106];
        var index = 0;

        foreach (var color in Enum.GetValues<TileColor>())
            for (var i = 1; i <= 13; i++)
            {
                tiles[index++] = new Tile(i, color);
                tiles[index++] = new Tile(i, color);
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

    private static void ShuffleSecure(Tile[] tiles)
    {
        for (var i = tiles.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }
    }

    private void DistributeTiles(Tile[] tiles, List<string> playerNames, IReadOnlyList<bool>? autoPlayers)
    {
        const int tilesPerPlayer = 14;

        var totalDistributed = playerNames.Count * tilesPerPlayer;
        if (totalDistributed > tiles.Length)
            throw new InvalidOperationException(
                $"Not enough tiles to deal {playerNames.Count} players.");

        for (var i = 0; i < playerNames.Count; i++)
        {
            var startIndex = i * tilesPerPlayer;
            var playerTiles = new Tile[tilesPerPlayer];
            Array.Copy(tiles, startIndex, playerTiles, 0, tilesPerPlayer);

            Players.Add(new GamePlayer
            {
                Name = playerNames[i],
                Rack = new Set(playerTiles.ToList()),
                IsAuto = autoPlayers is not null && i < autoPlayers.Count && autoPlayers[i]
            });
        }

        for (var i = totalDistributed; i < tiles.Length; i++)
            _tilePool.Enqueue(tiles[i]);
    }

    private record SolveAcceptedDto(string? ContentHash, string? RequestId, string? Status);
}
