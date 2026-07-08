using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using RummiHub.Core.Models;
using RummiHub.Web.Hubs;
using RummiHub.Web.Models;
using RummiHub.Web.Rendering;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Timers.Timer;

namespace RummiHub.Web.Components.Pages;

public partial class GamePage : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private bool _isConnected;
    private GameStateDto? _gameState;
    private List<Tile> _rackTiles = [];
    private string? _error;
    private string? _errorPlay;
    private bool _isLoading;
    private Tile? _drawnTile;
    private bool _showDrawnTileToast;
    private Timer? _toastTimer;
    private HashSet<int>? _highlightIndices;
    private Timer? _highlightTimer;
    private List<Tile>? _optimalPlayTiles;
    private bool _optimalSeenFirstUpdate;
    private bool _optimalReady;
    private List<Tile>? _optimalSuggestion;
    private HelperMode _helperMode = HelperMode.Hint;
    private bool _seedSaved;
    private bool _loadingAutoContinue;
    private List<int>? _lastHighlightIndices;
    private int _lastHighlightMs;

    // Whole-page tile animation module (wwwroot/js/tile-animations.js). It runs a FLIP pass,
    // one tile at a time, after every render, diffing DOM positions itself.
    private IJSObjectReference? _tileAnimations;

    [Inject] private IServer Server { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;

    [Parameter] public string? GameId { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "player")]
    public string? PlayerName { get; set; }

    private bool IsMyTurn =>
        _gameState is { IsGameOver: false } &&
        _gameState.CurrentPlayerIndex < _gameState.Players.Count &&
        _gameState.Players[_gameState.CurrentPlayerIndex].Name == PlayerName;

    /// <summary>I played in review mode — the turn waits for my confirmation.</summary>
    private bool AwaitingMyConfirmation =>
        _gameState is { AwaitingConfirmation: true } && IsMyTurn;

    /// <summary>Can I act (select tiles, play, draw) right now?</summary>
    private bool CanAct => IsMyTurn && !AwaitingMyConfirmation;

    private string? CurrentPlayerName =>
        _gameState != null && _gameState.CurrentPlayerIndex < _gameState.Players.Count
            ? _gameState.Players[_gameState.CurrentPlayerIndex].Name
            : null;

    private List<Tile> SelectedTilesForPlay { get; } = [];

    /// <summary>Solver assistance level — a per-player, client-side choice.</summary>
    private enum HelperMode
    {
        /// <summary>No solver help at all (no spinner, no hint).</summary>
        None,

        /// <summary>After acting, shows the optimal play you could have made (in the play zone).</summary>
        Hint,

        /// <summary>Pre-selects the solver's move as soon as it is ready — just press Play.</summary>
        Auto
    }

    private bool ShowHint => _helperMode == HelperMode.Hint && _optimalPlayTiles is { Count: > 0 };

    protected override async Task OnInitializedAsync()
    {
        if (string.IsNullOrWhiteSpace(GameId) || string.IsNullOrWhiteSpace(PlayerName))
        {
            Navigation.NavigateTo("/lobby");
            return;
        }

        var addresses = Server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var baseAddress = addresses?.FirstOrDefault(a => a.StartsWith("http://"))
                          ?? addresses?.FirstOrDefault();

        if (baseAddress is null)
        {
            _error = "Could not determine server address.";
            return;
        }

        var hubUri = new Uri(new Uri(baseAddress), "/hubs/game");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri)
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .WithAutomaticReconnect()
            .Build();

        RegisterHubEvents();

        try
        {
            await _hubConnection.StartAsync();
            _isConnected = true;
            await _hubConnection.SendAsync(HubEvents.JoinGameSession, GameId, PlayerName);
        }
        catch (Exception ex)
        {
            _error = $"Could not connect: {ex.Message}";
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        try
        {
            if (firstRender)
            {
                _tileAnimations = await JS.InvokeAsync<IJSObjectReference>("import", "./js/tile-animations.js");
                await _tileAnimations.InvokeVoidAsync(
                    "configure", BoardPacing.MoveMs, BoardPacing.PopMs, BoardPacing.StartIntervalMs);
            }

            // The module diffs tile positions against the previous render, so it needs to run
            // after every render. It animates only tiles that actually moved.
            if (_tileAnimations is not null)
                await _tileAnimations.InvokeVoidAsync("animate");
        }
        catch (Exception e) when (e is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // Animations are best-effort: never let a JS hiccup break the page.
        }
    }

    private void RegisterHubEvents()
    {
        var hub = _hubConnection!;

        hub.On<GameStateDto>(HubEvents.GameStateUpdated, state =>
        {
            _gameState = state;
            _errorPlay = null;
            _isLoading = false;
            // Cleared here rather than on RackUpdated so the selected tiles are still
            // on-screen one render earlier — their positions are what the played tiles
            // fly from when the new board arrives in this same update.
            ClearSelection();

            // The highlight always arrives AFTER its own state update, so any state seen
            // while one is showing means the next move landed: clear it. The suggestion
            // is per-turn; a new one arrives with the next OptimalReady.
            _highlightIndices = null;
            _lastHighlightIndices = null;
            StopHighlightTimer();
            _optimalSuggestion = null;

            if (_optimalPlayTiles != null)
            {
                if (!_optimalSeenFirstUpdate)
                    _optimalSeenFirstUpdate = true;
                else
                {
                    _optimalPlayTiles = null;
                    _optimalSeenFirstUpdate = false;
                }
            }

            InvokeAsync(StateHasChanged);
        });

        hub.On<List<int>, int>(HubEvents.HighlightTilesUpdated, (indices, displayMs) =>
        {
            // Reading-order indices of the tiles that entered the board (server-computed),
            // so the freshly-played copy lights up — never an identical twin already there.
            // The server sizes displayMs to the board animation plus a reading window; the
            // highlight then fades on its own instead of lingering until the next move.
            _highlightIndices = indices is { Count: > 0 } ? [..indices] : null;
            // Remembered so a "replay animation" can bring the highlight back too.
            _lastHighlightIndices = _highlightIndices?.ToList();
            _lastHighlightMs = displayMs;
            SetupHighlightTimer(displayMs);
            InvokeAsync(StateHasChanged);
        });

        hub.On<List<Tile>>(HubEvents.OptimalPlay, tiles =>
        {
            _optimalPlayTiles = tiles is { Count: > 0 } ? tiles : null;
            _optimalSeenFirstUpdate = false;
            // Player has just acted — the indicator is for next turn (someone else's).
            _optimalReady = false;
            InvokeAsync(StateHasChanged);
        });

        hub.On<List<Tile>>(HubEvents.OptimalReady, tiles =>
        {
            _optimalReady = true;
            _optimalSuggestion = tiles is { Count: > 0 } ? tiles : null;
            ApplyAutoPick();
            InvokeAsync(StateHasChanged);
        });

        hub.On<string>(HubEvents.SeedSaved, _ =>
        {
            _seedSaved = true;
            InvokeAsync(StateHasChanged);
        });

        hub.On<List<Tile>>(HubEvents.RackUpdated, rack =>
        {
            _rackTiles = rack;
            InvokeAsync(StateHasChanged);
        });

        hub.On<Tile>(HubEvents.TileDrawn, tile =>
        {
            _drawnTile = tile;
            _showDrawnTileToast = true;
            SetupToastTimer();
            InvokeAsync(StateHasChanged);
        });

        hub.On<string>(HubEvents.InvalidPlay, message =>
        {
            _errorPlay = message;
            _isLoading = false;
            InvokeAsync(StateHasChanged);
        });

        hub.On<string>(HubEvents.Error, message =>
        {
            _error = message;
            _isLoading = false;
            InvokeAsync(StateHasChanged);
        });
    }

    // --- Player actions ---

    /// <summary>
    /// Plays the selection. With <paramref name="autoContinue"/> the turn passes right away;
    /// without it, the server pauses after the move so the animation can be watched,
    /// replayed, and the turn confirmed by hand.
    /// </summary>
    private async Task OnPlaySelection(bool autoContinue)
    {
        if (SelectedTilesForPlay.Count == 0 || _hubConnection is null) return;

        _isLoading = true;
        _loadingAutoContinue = autoContinue;
        _errorPlay = null;
        StateHasChanged();

        try
        {
            await _hubConnection.SendAsync(
                HubEvents.PlayTiles, GameId, SelectedTilesForPlay.ToList(), autoContinue);
        }
        catch (Exception ex)
        {
            _errorPlay = $"Error: {ex.Message}";
            _isLoading = false;
        }
    }

    private async Task OnConfirmEndTurn()
    {
        if (_hubConnection is null || !AwaitingMyConfirmation) return;

        _isLoading = true;
        StateHasChanged();

        try
        {
            await _hubConnection.SendAsync(HubEvents.ConfirmEndTurn, GameId);
        }
        catch (Exception ex)
        {
            _errorPlay = $"Error: {ex.Message}";
            _isLoading = false;
        }
    }

    /// <summary>Replays the last board transition (and its played-tiles highlight).</summary>
    private async Task OnReplayAnimation()
    {
        if (_tileAnimations is null) return;

        try
        {
            var replayed = await _tileAnimations.InvokeAsync<bool>("replayLast");
            if (replayed && _lastHighlightIndices is { Count: > 0 })
            {
                _highlightIndices = [.._lastHighlightIndices];
                SetupHighlightTimer(_lastHighlightMs);
                StateHasChanged();
            }
        }
        catch (Exception e) when (e is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // Best-effort, like the rest of the animation layer.
        }
    }

    private async Task OnDrawTile()
    {
        if (_hubConnection is null) return;

        _isLoading = true;
        StateHasChanged();

        try
        {
            await _hubConnection.SendAsync(HubEvents.DrawTile, GameId);
        }
        catch (Exception ex)
        {
            _errorPlay = $"Error: {ex.Message}";
            _isLoading = false;
        }
    }

    private async Task OnSaveSeed()
    {
        if (_hubConnection is null || _seedSaved) return;

        try
        {
            await _hubConnection.SendAsync(HubEvents.SaveSeed, GameId);
        }
        catch (Exception ex)
        {
            _error = $"Error: {ex.Message}";
        }
    }

    // --- Keyboard shortcuts ---

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (!IsMyTurn || _isLoading) return;

        if (AwaitingMyConfirmation)
        {
            if (e.Key == "Enter")
                await OnConfirmEndTurn();
            return;
        }

        switch (e.Key)
        {
            case "Enter":
                if (SelectedTilesForPlay.Count > 0)
                    await OnPlaySelection(autoContinue: true);
                else if (_gameState is { TilePoolCount: > 0 })
                    await OnDrawTile();
                break;
            case "Escape":
                if (SelectedTilesForPlay.Count > 0)
                    OnClearSelection();
                break;
        }
    }

    // --- Tile selection logic ---

    private void SetHelperMode(HelperMode mode)
    {
        _helperMode = mode;
        // Switching to Auto mid-turn applies the already-received suggestion right away.
        ApplyAutoPick();
    }

    /// <summary>
    /// Auto-pick mode: pre-selects the solver's move so the player just presses Play.
    /// Never overwrites tiles the player already picked by hand.
    /// </summary>
    private void ApplyAutoPick()
    {
        if (_helperMode != HelperMode.Auto || !CanAct) return;
        if (SelectedTilesForPlay.Count > 0 || _optimalSuggestion is not { Count: > 0 }) return;
        SelectedTilesForPlay.AddRange(_optimalSuggestion);
    }

    private void OnClearSelection()
    {
        ClearSelection();
        StateHasChanged();
    }

    private void ClearSelection()
    {
        SelectedTilesForPlay.Clear();
    }

    private void OnSelectedTileClick(Tile tile)
    {
        // The FLIP pass animates the tile flying back to its rack stack.
        SelectedTilesForPlay.Remove(tile);
        StateHasChanged();
    }

    private void OnTileStackClick(Tile tileType)
    {
        if (!CanAct) return;

        // Each click selects one more copy; a click past the last copy clears them all.
        var totalAvailable = _rackTiles.Count(t => t.Equals(tileType));
        if (GetSelectedCountForTile(tileType) < totalAvailable)
            SelectedTilesForPlay.Add(tileType);
        else
            SelectedTilesForPlay.RemoveAll(t => t.Equals(tileType));

        StateHasChanged();
    }

    private int GetSelectedCountForTile(Tile tileType) =>
        SelectedTilesForPlay.Count(t => t.Equals(tileType));

    private IEnumerable<IGrouping<Tile, Tile>> GetGroupedTiles() =>
        _rackTiles
            .OrderBy(t => t.IsJoker ? 1 : 0)
            .ThenBy(t => t.Color)
            .ThenBy(t => t.Value)
            .GroupBy(t => t)
            .OrderBy(g => g.Key.IsJoker ? 1 : 0)
            .ThenBy(g => g.Key.Color)
            .ThenBy(g => g.Key.Value);

    // --- Played-tiles highlight ---

    private void SetupHighlightTimer(int displayMs)
    {
        StopHighlightTimer();
        if (_highlightIndices is null) return;

        _highlightTimer = new Timer(Math.Max(displayMs, 1000)) { AutoReset = false };
        _highlightTimer.Elapsed += (_, _) =>
        {
            InvokeAsync(() =>
            {
                _highlightIndices = null;
                StopHighlightTimer();
                StateHasChanged();
            });
        };
        _highlightTimer.Start();
    }

    private void StopHighlightTimer()
    {
        _highlightTimer?.Stop();
        _highlightTimer?.Dispose();
        _highlightTimer = null;
    }

    // --- Toast ---

    private void SetupToastTimer()
    {
        _toastTimer?.Stop();
        _toastTimer?.Dispose();
        _toastTimer = new Timer(3000);
        _toastTimer.Elapsed += (_, _) =>
        {
            InvokeAsync(() =>
            {
                _showDrawnTileToast = false;
                _drawnTile = null;
                _toastTimer?.Stop();
                _toastTimer?.Dispose();
                _toastTimer = null;
                StateHasChanged();
            });
        };
        _toastTimer.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _toastTimer?.Dispose();
        _highlightTimer?.Dispose();

        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();

        if (_tileAnimations is not null)
        {
            try
            {
                await _tileAnimations.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already closed — nothing to clean up client-side.
            }
        }
    }
}
