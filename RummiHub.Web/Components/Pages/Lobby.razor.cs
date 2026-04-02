using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using RummiHub.Web.Hubs;

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
        // The SignalR client runs server-side (InteractiveServer), so connect
        // directly to our own HTTP endpoint — no SSL, no certificate issues.
        var addresses = Server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var baseAddress = addresses?.FirstOrDefault(a => a.StartsWith("http://"))
                          ?? addresses?.FirstOrDefault();

        if (baseAddress is null)
        {
            _error = "Impossible de déterminer l'adresse du serveur.";
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
            _error = $"Impossible de se connecter au serveur : {ex.Message}";
        }
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

    private async Task StartGame()
    {
        if (_hubConnection is not null && _gameId is not null)
            await _hubConnection.SendAsync(HubEvents.StartGame, _gameId);
    }
}