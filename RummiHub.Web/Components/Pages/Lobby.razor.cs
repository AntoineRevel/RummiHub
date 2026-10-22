using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using RummiHub.Web.Hubs;
using RummiHub.Web.Services;

namespace RummiHub.Web.Components.Pages;

public partial class Lobby
{
    [Parameter]
    [SupplyParameterFromQuery(Name = "code")]
    public string? GameCode { get; set; }

    private string? _error;
    private string? _gameId;
    private HubConnection? _hubConnection;
    private bool _isConnected;
    private bool _isHost;
    private string _joinGameId = string.Empty;
    private string _playerName = string.Empty;
    private List<string> _players = [];
    private List<string> _availableGameIds = [];
    private string _selectedGameId = string.Empty;
    private ElementReference _nameInput;
    private bool _hasFocused;

    private static readonly string[] DefaultNames =
    [
        "Alice", "Bob", "Charlie", "Diana", "Eve",
        "Frank", "Grace", "Henry", "Iris", "Jack"
    ];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!_hasFocused && _isConnected && string.IsNullOrEmpty(_gameId))
        {
            _hasFocused = true;
            await _nameInput.FocusAsync();
        }
    }

    protected override void OnParametersSet()
    {
        if (!string.IsNullOrWhiteSpace(GameCode))
            _joinGameId = GameCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();
    }

    protected override async Task OnInitializedAsync()
    {
        _playerName = DefaultNames[Random.Shared.Next(DefaultNames.Length)];

        var baseAddress = InProcessHubAddress.TryGetBaseAddress(
            Server.Features.Get<IServerAddressesFeature>()?.Addresses);

        if (baseAddress is null)
        {
            _error = "Could not determine server address.";
            return;
        }

        var hubUri = new Uri(new Uri(baseAddress), "/hubs/game");

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri)
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<IEnumerable<string>>(HubEvents.AvailableGameIds, ids =>
        {
            _availableGameIds = ids.ToList();
            InvokeAsync(StateHasChanged);
        });

        _hubConnection.On<string, string>(HubEvents.SeatIssued, (gameId, token) =>
            InvokeAsync(async () =>
            {
                await RememberSeatTokenAsync(gameId, token);
                StateHasChanged();
            }));

        _hubConnection.On<string, IEnumerable<string>>(HubEvents.GameCreated, (gameId, players) =>
        {
            _gameId = gameId;
            _players = players.ToList();
            _isHost = true;
            _error = null;
            InvokeAsync(StateHasChanged);
        });

        _hubConnection.On<string, IEnumerable<string>>(HubEvents.PlayerJoined, (playerName, players) =>
        {
            _players = players.ToList();
            if (_gameId == null) _gameId = _joinGameId;
            _error = null;
            InvokeAsync(StateHasChanged);
        });

        _hubConnection.On<IEnumerable<string>>(HubEvents.PlayerLeft, players =>
        {
            _players = players.ToList();
            InvokeAsync(StateHasChanged);
        });

        _hubConnection.On<string, IEnumerable<string>>(HubEvents.GameStarted, (gameId, _) =>
        {
            InvokeAsync(() =>
                Navigation.NavigateTo($"/game/{gameId}?player={Uri.EscapeDataString(_playerName)}"));
        });

        _hubConnection.On<string>(HubEvents.Error, message =>
        {
            _error = message;
            InvokeAsync(StateHasChanged);
        });

        try
        {
            await _hubConnection.StartAsync();
            _isConnected = true;
            await _hubConnection.SendAsync(HubEvents.GetAvailableGameIds);
        }
        catch (Exception ex)
        {
            _error = $"Could not connect to server: {ex.Message}";
        }
    }

    /// <summary>
    /// Keeps the seat secret in this browser tab so the game page can reclaim the seat
    /// after navigation and after a refresh. The secret is not put on the game URL.
    /// </summary>
    private async Task RememberSeatTokenAsync(string gameId, string token)
    {
        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/seat-token.js");
            try
            {
                await module.InvokeVoidAsync("save", gameId, token);
            }
            finally
            {
                await module.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _error = $"Could not store the seat secret in this browser: {ex.Message}";
        }
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_playerName))
            await HandleAction();
    }

    private async Task HandleAction()
    {
        if (string.IsNullOrWhiteSpace(_playerName)) return;

        if (string.IsNullOrWhiteSpace(_joinGameId))
            await CreateGame();
        else
            await JoinGame();
    }

    private async Task CreateGame()
    {
        if (_hubConnection is not null)
        {
            var seed = string.IsNullOrWhiteSpace(_selectedGameId) ? null : _selectedGameId;
            await _hubConnection.SendAsync(HubEvents.CreateGame, _playerName, seed);
        }
    }

    private async Task JoinGame()
    {
        if (_hubConnection is not null)
            await _hubConnection.SendAsync(HubEvents.JoinGame, _joinGameId, _playerName);
    }

    private async Task AddAiPlayer()
    {
        if (_hubConnection is not null && _gameId is not null)
            await _hubConnection.SendAsync(HubEvents.AddAiPlayer, _gameId);
    }

    private async Task StartGame()
    {
        if (_hubConnection is not null && _gameId is not null)
            await _hubConnection.SendAsync(HubEvents.StartGame, _gameId);
    }
}
