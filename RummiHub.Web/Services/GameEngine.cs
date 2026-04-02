using System.Net.Http.Json;
using RummiHub.Core.Contracts;
using RummiHub.Core.Models;

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

    public void Initialize(List<string> playerNames, string? distributionSeed = null)
    {
        Id = Guid.NewGuid();
        Turn = 0;
        PlayerIndex = 0;
        IsGameOver = false;
        Board = new Solution();
        Players.Clear();
        _tilePool.Clear();

        var tiles = GenerateTiles();
        var rng = distributionSeed is not null
            ? new Random(distributionSeed.GetHashCode())
            : new Random();
        Shuffle(tiles, rng);
        DistributeTiles(tiles, playerNames);
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
        var player = CurrentPlayer;
        var request = new SolveRequest
        {
            RequestId = $"game-{Id.ToString()[..8]}-t{Turn}-p{PlayerIndex}",
            Hand = player.Rack.GetAllTilesIncludingJokers().ToList(),
            Board = Board,
            HasPlayed = player.HasPlayed
        };

        return await SubmitAndWaitForResult(httpClient, resultSubscriber, request, ct);
    }

    /// <summary>
    /// Validates a human player's tile selection by sending it through the solver.
    /// Returns the SolveResponse. Caller checks that all selected tiles were used.
    /// </summary>
    public async Task<SolveResponse> ValidateHumanPlay(
        List<Tile> selectedTiles,
        HttpClient httpClient,
        SolveResultSubscriber resultSubscriber,
        CancellationToken ct)
    {
        var request = new SolveRequest
        {
            RequestId = $"validate-{Guid.NewGuid().ToString()[..8]}",
            Hand = selectedTiles,
            Board = Board,
            HasPlayed = CurrentPlayer.HasPlayed
        };

        return await SubmitAndWaitForResult(httpClient, resultSubscriber, request, ct);
    }

    /// <summary>
    /// Applies a successful solve result: updates board, removes played tiles from rack, checks win.
    /// </summary>
    public void ApplySolveResult(SolveResponse result)
    {
        if (!result.Found || result.NewBoard == null) return;

        var player = CurrentPlayer;

        // Remove played tiles from rack
        foreach (var tile in result.TilesToPlay)
            player.Rack.Remove(tile);

        for (var i = 0; i < result.JokersToPlay; i++)
            player.Rack.Remove(new Tile(isJoker: true));

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
            IsGameOver = true;
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

    private void DistributeTiles(Tile[] tiles, List<string> playerNames)
    {
        const int tilesPerPlayer = 14;

        for (var i = 0; i < playerNames.Count; i++)
        {
            var startIndex = i * tilesPerPlayer;
            var playerTiles = new Tile[tilesPerPlayer];
            Array.Copy(tiles, startIndex, playerTiles, 0, tilesPerPlayer);

            Players.Add(new GamePlayer
            {
                Name = playerNames[i],
                Rack = new Set(playerTiles.ToList())
            });
        }

        var totalDistributed = playerNames.Count * tilesPerPlayer;
        for (var i = totalDistributed; i < tiles.Length; i++)
            _tilePool.Enqueue(tiles[i]);
    }

    private record SolveAcceptedDto(string? ContentHash, string? RequestId, string? Status);
}
