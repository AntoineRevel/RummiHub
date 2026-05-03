using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using RummiHub.Core.Models;
using RummiHub.Web.Hubs;
using RummiHub.Web.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timer = System.Timers.Timer;

namespace RummiHub.Web.Components.Pages;

public partial class GamePage : IAsyncDisposable
{
    private readonly HashSet<Guid> _newTiles = [];
    private readonly HashSet<Guid> _removingTiles = [];
    private readonly List<TileInstance> _selectedTileInstances = [];

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
    private List<Tile>? _highlightTiles;
    private bool _highlightSeenFirstUpdate;
    private List<Tile>? _optimalPlayTiles;
    private bool _optimalSeenFirstUpdate;
    private bool _optimalReady;

    [Inject] private IServer Server { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    [Parameter] public string? GameId { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "player")]
    public string? PlayerName { get; set; }

    private bool IsMyTurn =>
        _gameState is { IsGameOver: false } &&
        _gameState.CurrentPlayerIndex < _gameState.Players.Count &&
        _gameState.Players[_gameState.CurrentPlayerIndex].Name == PlayerName;

    private string? CurrentPlayerName =>
        _gameState != null && _gameState.CurrentPlayerIndex < _gameState.Players.Count
            ? _gameState.Players[_gameState.CurrentPlayerIndex].Name
            : null;

    private List<Tile> SelectedTilesForPlay { get; } = [];

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

    private void RegisterHubEvents()
    {
        var hub = _hubConnection!;

        hub.On<GameStateDto>(HubEvents.GameStateUpdated, state =>
        {
            _gameState = state;
            _errorPlay = null;
            _isLoading = false;

            if (_highlightTiles != null)
            {
                if (!_highlightSeenFirstUpdate)
                    _highlightSeenFirstUpdate = true;
                else
                {
                    _highlightTiles = null;
                    _highlightSeenFirstUpdate = false;
                }
            }

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

        hub.On<List<Tile>>(HubEvents.HighlightTilesUpdated, tiles =>
        {
            _highlightTiles = tiles;
            _highlightSeenFirstUpdate = false;
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

        hub.On(HubEvents.OptimalReady, () =>
        {
            _optimalReady = true;
            InvokeAsync(StateHasChanged);
        });

        hub.On<List<Tile>>(HubEvents.RackUpdated, rack =>
        {
            _rackTiles = rack;
            ClearSelection();
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

    private async Task OnPlaySelection()
    {
        if (SelectedTilesForPlay.Count == 0 || _hubConnection is null) return;

        _isLoading = true;
        _errorPlay = null;
        StateHasChanged();

        try
        {
            await _hubConnection.SendAsync(HubEvents.PlayTiles, GameId, SelectedTilesForPlay.ToList());
        }
        catch (Exception ex)
        {
            _errorPlay = $"Error: {ex.Message}";
            _isLoading = false;
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

    // --- Keyboard shortcuts ---

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (!IsMyTurn || _isLoading) return;

        switch (e.Key)
        {
            case "Enter":
                if (SelectedTilesForPlay.Count > 0)
                    await OnPlaySelection();
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

    private void OnClearSelection()
    {
        ClearSelection();
        StateHasChanged();
    }

    private void ClearSelection()
    {
        _selectedTileInstances.Clear();
        SelectedTilesForPlay.Clear();
        _removingTiles.Clear();
        _newTiles.Clear();
    }

    private async Task OnTileInstanceClick(TileInstance tileInstance)
    {
        _removingTiles.Add(tileInstance.Id);
        StateHasChanged();
        await Task.Delay(300);
        _selectedTileInstances.Remove(tileInstance);
        _removingTiles.Remove(tileInstance.Id);
        SyncSelectedTiles();
        StateHasChanged();
    }

    private void SyncSelectedTiles()
    {
        SelectedTilesForPlay.Clear();
        SelectedTilesForPlay.AddRange(_selectedTileInstances.Select(ti => ti.Tile));
    }

    private async Task OnTileStackClick(Tile tileType)
    {
        var totalAvailable = _rackTiles.Count(t => t.Equals(tileType));
        var currentSelectedCount = GetSelectedCountForTile(tileType);
        var newSelectedCount = (currentSelectedCount + 1) % (totalAvailable + 1);

        if (newSelectedCount > currentSelectedCount)
        {
            var tilesToAdd = newSelectedCount - currentSelectedCount;
            for (var i = 0; i < tilesToAdd; i++)
            {
                var newInstance = new TileInstance(tileType);
                _selectedTileInstances.Add(newInstance);
                _newTiles.Add(newInstance.Id);
            }
        }
        else if (newSelectedCount < currentSelectedCount)
        {
            var tilesToRemove = currentSelectedCount - newSelectedCount;
            for (var i = 0; i < tilesToRemove; i++)
            for (var j = _selectedTileInstances.Count - 1; j >= 0; j--)
            {
                if (!_selectedTileInstances[j].Tile.Equals(tileType)) continue;
                _selectedTileInstances.RemoveAt(j);
                break;
            }
        }
        else
        {
            var instancesToRemove = _selectedTileInstances.Where(ti => ti.Tile.Equals(tileType)).ToList();
            foreach (var instance in instancesToRemove)
            {
                _selectedTileInstances.Remove(instance);
                _newTiles.Remove(instance.Id);
            }
        }

        SyncSelectedTiles();
        StateHasChanged();

        if (newSelectedCount > currentSelectedCount)
        {
            await Task.Delay(300);
            var instancesToClean = _selectedTileInstances
                .Where(ti => ti.Tile.Equals(tileType) && _newTiles.Contains(ti.Id)).ToList();
            foreach (var instance in instancesToClean) _newTiles.Remove(instance.Id);
            StateHasChanged();
        }
    }

    private int GetSelectedCountForTile(Tile tileType) =>
        _selectedTileInstances.Count(ti => ti.Tile.Equals(tileType));

    private IEnumerable<IGrouping<Tile, Tile>> GetGroupedTiles() =>
        _rackTiles
            .OrderBy(t => t.IsJoker ? 1 : 0)
            .ThenBy(t => t.Color)
            .ThenBy(t => t.Value)
            .GroupBy(t => t)
            .OrderBy(g => g.Key.IsJoker ? 1 : 0)
            .ThenBy(g => g.Key.Color)
            .ThenBy(g => g.Key.Value);

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
        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();
    }

    private readonly record struct TileInstance(Tile Tile)
    {
        public Tile Tile { get; } = Tile;
        public Guid Id { get; } = Guid.NewGuid();
    }
}
