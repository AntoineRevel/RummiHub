using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using RummiHub.Web.Hubs;
using RummiHub.Web.Models;
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
    private List<SeedListItemDto> _seeds = [];

    /// <summary>The seed select: <see cref="RandomSeed"/>, <see cref="PasteSeed"/> or a listed seed.</summary>
    private string _seedChoice = RandomSeed;
    private string _pastedSeed = string.Empty;

    /// <summary>The seed the game was created with, shown in the waiting room.</summary>
    private string? _gameSeed;

    private const string RandomSeed = "";
    private const string PasteSeed = "paste";
    private ElementReference _nameInput;
    private ElementReference _startButton;
    private bool _startArmed;
    private bool _hasFocused;
    private bool _inviteCopied;
    private CancellationTokenSource? _inviteCopiedResetCts;
    private bool _gameStarted;
    private string? _gameUrl;
    /// <summary>This tab's seat secret, handed to the game tab in <see cref="_gameUrl"/>'s fragment.</summary>
    private string? _seatToken;

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

        if (!_startArmed && _isHost && !_gameStarted)
        {
            _startArmed = true;
            await InvokeSeatModuleAsync(m => m.InvokeVoidAsync("armStart", _startButton));
        }
    }

    protected override void OnParametersSet()
    {
        if (!string.IsNullOrWhiteSpace(GameCode))
            _joinGameId = GameCode;
    }

    public async ValueTask DisposeAsync()
    {
        _inviteCopiedResetCts?.Cancel();
        _inviteCopiedResetCts?.Dispose();
        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();
    }

    private async Task CopyInviteLink()
    {
        if (string.IsNullOrEmpty(_gameId)) return;

        var url = Navigation.ToAbsoluteUri($"/lobby?code={Uri.EscapeDataString(_gameId)}").ToString();
        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", url);
            _inviteCopied = true;
            StateHasChanged();

            _inviteCopiedResetCts?.Cancel();
            _inviteCopiedResetCts?.Dispose();
            _inviteCopiedResetCts = new CancellationTokenSource();
            var token = _inviteCopiedResetCts.Token;
            _ = ResetInviteCopiedAsync(token);
        }
        catch (Exception ex)
        {
            _error = $"Could not copy invite link: {ex.Message}";
        }
    }

    private async Task ResetInviteCopiedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(2000, token);
            _inviteCopied = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // A newer copy reset the timer.
        }
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

        _hubConnection.On<List<SeedListItemDto>>(HubEvents.SeedList, seeds =>
        {
            _seeds = seeds;
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
            // The fragment never reaches the server; the game page moves it into its
            // sessionStorage and strips it from the address bar (seat-token.js).
            _gameUrl = $"/game/{gameId}?player={Uri.EscapeDataString(_playerName)}"
                       + (_seatToken is null ? "" : $"#seat={_seatToken}");
            _gameStarted = true;
            InvokeAsync(async () =>
            {
                await OpenGameAsync();
                StateHasChanged();
            });
        });

        _hubConnection.On<string>(HubEvents.Error, message =>
        {
            _error = message;
            InvokeAsync(async () =>
            {
                // A failed Start leaves the blank tab its click opened with nothing to show.
                await InvokeSeatModuleAsync(m => m.InvokeVoidAsync("discardPendingTab"));
                StateHasChanged();
            });
        });

        try
        {
            await _hubConnection.StartAsync();
            _isConnected = true;
            await _hubConnection.SendAsync(HubEvents.GetSeedList);
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
        _seatToken = token;
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

    /// <summary>The seed the next game is created with: null for a random one.</summary>
    private string? ChosenSeed =>
        _seedChoice switch
        {
            RandomSeed => null,
            PasteSeed => DistributionSeeds.TryNormalize(_pastedSeed, out var seed, out _) ? seed : null,
            var listed => listed
        };

    /// <summary>Why the pasted seed is refused; null while nothing is typed or once it is valid.</summary>
    private string? PasteError =>
        _seedChoice == PasteSeed && !string.IsNullOrWhiteSpace(_pastedSeed)
        && !DistributionSeeds.TryNormalize(_pastedSeed, out _, out var error)
            ? error
            : null;

    private bool CanCreate =>
        !string.IsNullOrWhiteSpace(_playerName) && (_seedChoice != PasteSeed || ChosenSeed is not null);

    private static string SeedOptionLabel(SeedListItemDto item) =>
        $"{item.AddedAt.UtcDateTime:d MMM yyyy} · {DistributionSeeds.Abbreviate(item.Seed)}";

    private async Task CreateGame()
    {
        if (_hubConnection is null || !CanCreate)
            return;

        _gameSeed = ChosenSeed;
        await _hubConnection.SendAsync(HubEvents.CreateGame, _playerName, _gameSeed);
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

    /// <summary>
    /// Opens the game when it starts (seat-token.js): in the tab the host's Start click opened,
    /// else in a new tab, else, when the browser blocks it (Safari, popup blockers: a player
    /// who joined has no click to open a tab with), in this tab, so no browser setting is needed.
    /// </summary>
    private Task OpenGameAsync() =>
        _gameUrl is null ? Task.CompletedTask : InvokeSeatModuleAsync(m => m.InvokeVoidAsync("openGame", _gameUrl));

    private async Task InvokeSeatModuleAsync(Func<IJSObjectReference, ValueTask> call)
    {
        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/seat-token.js");
            try
            {
                await call(module);
            }
            finally
            {
                await module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // The circuit is gone or the browser refused: the "Open the game" link remains.
        }
    }
}
