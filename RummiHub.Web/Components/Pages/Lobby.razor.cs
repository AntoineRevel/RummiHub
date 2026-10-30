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
    private List<LobbyPlayerDto> _players = [];
    private List<SeedListItemDto> _seeds = [];

    /// <summary>The seed select: <see cref="RandomSeed"/>, <see cref="PasteSeed"/> or a listed seed.</summary>
    private string _seedChoice = RandomSeed;
    private string _pastedSeed = string.Empty;

    /// <summary>The short form of the seed the game was created with, shown in the waiting room.</summary>
    private string? _gameSeed;

    /// <summary>The list entry the game replays, if it was created from one.</summary>
    private SeedListItemDto? _gameSaved;

    /// <summary>The new name typed for the selected list entry; null when not renaming.</summary>
    private string? _renameDraft;

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

        _hubConnection.On<string, List<LobbyPlayerDto>>(HubEvents.GameCreated, (gameId, players) =>
        {
            _gameId = gameId;
            _players = players;
            _isHost = true;
            _error = null;
            InvokeAsync(async () =>
            {
                StateHasChanged();
                await JS.TrackAsync(StatsEvents.GameCreated);
            });
        });

        _hubConnection.On<string, List<LobbyPlayerDto>>(HubEvents.PlayerJoined, (playerName, players) =>
        {
            _players = players;
            var joined = _gameId == null;
            if (joined) _gameId = _joinGameId;
            _error = null;
            InvokeAsync(async () =>
            {
                StateHasChanged();
                if (joined)
                    await JS.TrackAsync(StatsEvents.GameJoined);
            });
        });

        _hubConnection.On<List<LobbyPlayerDto>>(HubEvents.PlayerLeft, players =>
        {
            _players = players;
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
                // Once per game: only the host's lobby sends it (AI seats included in the count).
                if (_isHost)
                    await JS.TrackAsync(StatsEvents.GameStarted, new { joueurs = _players.Count });
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

    /// <summary>The pasted seed, normalized; null when not pasting or not valid yet.</summary>
    private string? PastedSeed =>
        _seedChoice == PasteSeed && DistributionSeeds.TryNormalize(_pastedSeed, out var seed, out _) ? seed : null;

    /// <summary>Why the pasted seed is refused; null while nothing is typed or once it is valid.</summary>
    private string? PasteError =>
        _seedChoice == PasteSeed && !string.IsNullOrWhiteSpace(_pastedSeed)
        && !DistributionSeeds.TryNormalize(_pastedSeed, out _, out var error)
            ? error
            : null;

    private bool IsListedSeed => _seedChoice is not (RandomSeed or PasteSeed);

    private SeedListItemDto? SelectedSeed => IsListedSeed ? _seeds.FirstOrDefault(s => s.Id == _seedChoice) : null;

    /// <summary>The select's value; picking another entry leaves a rename in progress.</summary>
    private string SeedChoice
    {
        get => _seedChoice;
        set
        {
            _seedChoice = value;
            _renameDraft = null;
        }
    }

    /// <summary>Asks first: a removed seed is gone for production and the test site alike.</summary>
    private async Task RemoveSelectedSeed()
    {
        if (_hubConnection is null || SelectedSeed is not { } selected)
            return;

        if (!await JS.InvokeAsync<bool>("confirm", $"Remove {SeedTitle(selected)} from the list?"))
            return;

        SeedChoice = RandomSeed;
        await _hubConnection.SendAsync(HubEvents.RemoveSeed, selected.Id);
    }

    private void StartRename() => _renameDraft = SelectedSeed?.Name ?? string.Empty;

    private void CancelRename() => _renameDraft = null;

    private async Task SaveRename()
    {
        if (_hubConnection is null || SelectedSeed is not { } selected || _renameDraft is null)
            return;

        var name = _renameDraft;
        _renameDraft = null;
        // The list comes back renamed; the entry keeps its id.
        await _hubConnection.SendAsync(HubEvents.RenameSeed, selected.Id, name);
    }

    private async Task HandleRenameKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter")
            await SaveRename();
        else if (e.Key == "Escape")
            CancelRename();
    }

    private bool CanCreate =>
        !string.IsNullOrWhiteSpace(_playerName) && (_seedChoice != PasteSeed || PastedSeed is not null);

    /// <summary>Its name, else its short seed.</summary>
    private static string SeedTitle(SeedListItemDto item) => item.Name ?? item.Short;

    /// <summary>What the deal replays: the table size and the seat (counted from 1) it was saved from.</summary>
    private static string SeedTable(SeedListItemDto item) => $"{item.Players} players, seat {item.Seat + 1}";

    private static string SeedOptionLabel(SeedListItemDto item) =>
        $"{SeedTitle(item)} · {SeedTable(item)} · {item.AddedAt.UtcDateTime:d MMM yyyy}";

    private async Task CreateGame()
    {
        if (_hubConnection is null || !CanCreate)
            return;

        var pasted = PastedSeed;
        _gameSaved = SelectedSeed;
        _gameSeed = pasted is not null ? DistributionSeeds.Abbreviate(pasted) : _gameSaved?.Short;
        await _hubConnection.SendAsync(HubEvents.CreateGame, _playerName, pasted, _gameSaved?.Id);
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

    private async Task RemoveAiPlayer(string name)
    {
        if (_hubConnection is not null && _gameId is not null)
            await _hubConnection.SendAsync(HubEvents.RemoveAiPlayer, _gameId, name);
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
