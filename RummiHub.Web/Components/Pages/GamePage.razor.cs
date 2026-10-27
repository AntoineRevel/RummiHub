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
using RummiHub.Web.Services;
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
    private RackSort _rackSort = RackSort.Color;
    private string? _error;
    private string? _errorPlay;
    private bool _isLoading;
    /// <summary>The tile I last drew, lit in the rack until I next touch it.</summary>
    private Tile? _drawnTile;
    private HashSet<int>? _highlightIndices;
    private HashSet<int>? _modifiedSetIndices;
    private Timer? _highlightTimer;
    private bool _optimalReady;
    private List<Tile>? _optimalSuggestion;
    /// <summary>The player asked for this turn's hint and it has not been applied yet.</summary>
    private bool _hintRequested;
    /// <summary>The asked-for hint has no tiles. Shown only while the play zone is empty.</summary>
    private bool _hintNoMove;
    private bool _seedSaved;
    private bool _seedCopied;
    private bool _loadingAutoContinue;
    private string? _seatToken;
    private bool _hubStartRequested;
    private List<int>? _lastHighlightIndices;
    private List<int>? _lastModifiedSetIndices;
    private int _lastHighlightMs;

    // The JS module kept the last board story (my review, an AI move, the winning move).
    // Flipped after animate(); the button is in the layout either way, so enabling it
    // does not shift the board under a running animation.
    private bool _canReplay;

    // While a replay is on screen the DOM must stay on that board: the next move (an AI's,
    // usually) is queued and applied once the replay has landed, then one at a time so each
    // still plays out. A win has no next move — the queue stays empty.
    private readonly List<HeldMove> _heldMoves = [];
    private Timer? _replayHoldTimer;
    private Timer? _heldDrainTimer;
    private bool _drainingHeld;
    private bool _releaseHeldAfterRender;
    private int _replayHoldGeneration;

    private bool HoldingMoves => _replayHoldTimer is not null || _drainingHeld;

    // My play's new rack, which the server sends BEFORE the board: held back until the board
    // arrives so the whole move renders as ONE transition — the played tiles leave the play
    // zone as they land on the board, and the copies left in the rack never blink out.
    private List<Tile>? _rackAwaitingBoard;

    // Someone else's move, held back from the turn display until the board has played it
    // out: the board shows it at once, whose turn it is (the players bar, the banner, my side
    // of the table) only once it has landed. Revealed by _turnRevealTimer.
    private GameStateDto? _turnAwaitingBoard;
    private Timer? _turnRevealTimer;

    // Whole-page tile animation module (wwwroot/js/tile-animations.js). It runs a FLIP pass
    // after every render, diffing DOM positions itself, and tells board changes as
    // play → make room → place.
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

    /// <summary>Hint was asked and the background solve has not answered yet.</summary>
    private bool HintPending => _hintRequested && !_optimalReady;

    /// <summary>The hint came back empty, and the player has not picked tiles since.</summary>
    private bool ShowDrawHint => _hintNoMove && !SelectedTilesForPlay.Any();

    protected override Task OnInitializedAsync()
    {
        if (string.IsNullOrWhiteSpace(GameId) || string.IsNullOrWhiteSpace(PlayerName))
            Navigation.NavigateTo("/lobby");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the hub once the browser can hand back the seat secret saved by the lobby.
    /// A refresh of this tab still has the secret; the game URL alone does not.
    /// </summary>
    private async Task EnsureHubAsync()
    {
        if (_hubStartRequested || string.IsNullOrWhiteSpace(GameId) || string.IsNullOrWhiteSpace(PlayerName))
            return;

        _hubStartRequested = true;

        try
        {
            var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/seat-token.js");
            try
            {
                _seatToken = await module.InvokeAsync<string?>("load", GameId);
            }
            finally
            {
                await module.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException)
        {
            _error = "Could not read the seat secret from this browser.";
            StateHasChanged();
            return;
        }

        if (string.IsNullOrEmpty(_seatToken))
        {
            _error = "This browser tab has no seat secret for this game. Join from the lobby in this same tab.";
            StateHasChanged();
            return;
        }

        var baseAddress = InProcessHubAddress.TryGetBaseAddress(
            Server.Features.Get<IServerAddressesFeature>()?.Addresses);

        if (baseAddress is null)
        {
            _error = "Could not determine server address.";
            StateHasChanged();
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
        _hubConnection.Reconnected += async _ =>
        {
            try
            {
                await _hubConnection.SendAsync(HubEvents.JoinGameSession, GameId, PlayerName, _seatToken);
            }
            catch (Exception ex)
            {
                _error = $"Could not reconnect to the game: {ex.Message}";
                await InvokeAsync(StateHasChanged);
            }
        };

        try
        {
            await _hubConnection.StartAsync();
            _isConnected = true;
            await _hubConnection.SendAsync(HubEvents.JoinGameSession, GameId, PlayerName, _seatToken);
        }
        catch (Exception ex)
        {
            _error = $"Could not connect: {ex.Message}";
        }

        StateHasChanged();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await LoadRackSortAsync();
            await EnsureHubAsync();
        }

        // Captured before awaiting JS: a state arriving meanwhile is drawn by the next render,
        // which schedules its own reveal from the board it actually shows.
        var turnAwaitingBoard = _turnAwaitingBoard;
        var boardBusyMs = 0;
        try
        {
            if (firstRender)
            {
                _tileAnimations = await JS.InvokeAsync<IJSObjectReference>("import", "./js/tile-animations.js");
                await _tileAnimations.InvokeVoidAsync("configure", new
                {
                    BoardPacing.MoveMs, BoardPacing.PopMs,
                    BoardPacing.LiftMs, BoardPacing.PauseMs, BoardPacing.RoomMs, BoardPacing.DropMs,
                    BoardPacing.FlightMs, BoardPacing.StaggerMs, BoardPacing.MaxStaggerSpreadMs
                });
            }

            // The module diffs tile positions against the previous render, so it needs to run
            // after every render. It animates only tiles that actually moved, and says how
            // long the board keeps moving.
            if (_tileAnimations is not null)
            {
                boardBusyMs = await _tileAnimations.InvokeAsync<int?>("animate") ?? 0;
                var canReplay = await _tileAnimations.InvokeAsync<bool>("canReplay");
                if (canReplay != _canReplay)
                {
                    _canReplay = canReplay;
                    StateHasChanged();
                }
            }
        }
        catch (Exception e) when (e is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // Animations are best-effort: never let a JS hiccup break the page.
        }

        if (turnAwaitingBoard is not null && ReferenceEquals(turnAwaitingBoard, _turnAwaitingBoard))
            ScheduleTurnReveal(boardBusyMs);

        // The move just released from the replay queue is on screen. The next one waits
        // until this board stops moving, so neither animation is skipped.
        if (_releaseHeldAfterRender && _replayHoldTimer is null)
        {
            _releaseHeldAfterRender = false;
            ScheduleHeldDrain(boardBusyMs);
        }
    }

    private void RegisterHubEvents()
    {
        var hub = _hubConnection!;

        // Every event applies on the renderer's synchronization context, in arrival order, then
        // re-renders: state never changes under a render, and a move's events (rack, state,
        // highlight) apply in the order the server sent them.
        void Handle<T>(string method, Action<T> apply) =>
            hub.On<T>(method, value => InvokeAsync(() => { apply(value); StateHasChanged(); }));

        void Handle3<T1, T2, T3>(string method, Action<T1, T2, T3> apply) =>
            hub.On<T1, T2, T3>(method, (a, b, c) => InvokeAsync(() => { apply(a, b, c); StateHasChanged(); }));

        Handle<GameStateDto>(HubEvents.GameStateUpdated, state =>
        {
            // A replay is showing this board. Keep it there; the move applies when it lands.
            if (HoldingMoves)
            {
                _heldMoves.Add(new HeldMove { State = state });
                return;
            }

            ApplyIncomingState(state);
        });

        Handle3<List<int>, int, List<int>>(HubEvents.HighlightTilesUpdated, (indices, displayMs, modifiedSets) =>
        {
            if (HoldingMoves)
            {
                // The highlight follows its state. With nothing queued it belongs to the
                // move on screen (it arrived while we were already replaying it).
                if (_heldMoves.Count == 0)
                {
                    ApplyIncomingHighlight(indices, displayMs, modifiedSets);
                    return;
                }

                if (!_heldMoves[^1].Highlighted)
                {
                    var move = _heldMoves[^1];
                    move.Highlighted = true;
                    move.Indices = indices is { Count: > 0 } ? [..indices] : null;
                    move.DisplayMs = displayMs;
                    move.ModifiedSets = modifiedSets is { Count: > 0 } ? [..modifiedSets] : null;
                }

                return;
            }

            ApplyIncomingHighlight(indices, displayMs, modifiedSets);
        });

        Handle<List<Tile>>(HubEvents.OptimalReady, tiles =>
        {
            // The suggestion follows its state. If that state is still parked behind a
            // replay, it waits with it: applied now, the state landing would wipe it.
            if (HoldingMoves && _heldMoves.Count > 0)
            {
                _heldMoves[^1].Optimal = tiles ?? [];
                return;
            }

            ApplyOptimal(tiles);
        });

        hub.On(HubEvents.SeedSaved, () => InvokeAsync(() => { _seedSaved = true; StateHasChanged(); }));

        Handle<List<Tile>>(HubEvents.RackUpdated, rack =>
        {
            // While my play is being validated, the selection is still shown as part of
            // the rack: the new rack (without the played tiles) waits for the board.
            if (_isLoading && SelectedTilesForPlay.Count > 0)
                _rackAwaitingBoard = rack;
            else
                _rackTiles = rack;
        });

        Handle<Tile>(HubEvents.TileDrawn, tile => _drawnTile = tile);

        Handle<string>(HubEvents.InvalidPlay, message =>
        {
            _errorPlay = message;
            _isLoading = false;
            ApplyRackAwaitingBoard();
        });

        Handle<string>(HubEvents.Error, message =>
        {
            _error = message;
            _isLoading = false;
            ApplyRackAwaitingBoard();
        });
    }

    private void ApplyIncomingState(GameStateDto state)
    {
        _errorPlay = null;
        _isLoading = false;
        // Cleared here rather than on RackUpdated so the selected tiles are still
        // on-screen one render earlier — their positions are what the played tiles
        // fly from when the new board arrives in this same update.
        ClearSelection();
        ApplyRackAwaitingBoard();

        // The highlight always arrives AFTER its own state update, so a state that changes
        // the board while one is showing means the next move landed: clear it. A state
        // that leaves the board as is (a draw, a turn confirmation) keeps the last move
        // lit until its timer runs out, so it can still be read. The suggestion is
        // per-turn; a new one arrives with the next OptimalReady.
        if (_gameState is null || !BoardPacing.SameBoard(_gameState.Board, state.Board))
        {
            _highlightIndices = null;
            _modifiedSetIndices = null;
            _lastHighlightIndices = null;
            _lastModifiedSetIndices = null;
            StopHighlightTimer();
        }
        _optimalSuggestion = null;
        _optimalReady = false;
        _hintRequested = false;
        _hintNoMove = false;

        // A newer state supersedes a turn still waiting for the board.
        RevealTurn();

        if (_gameState is not null && !IsMyTurn)
        {
            // Someone else's move: the board plays it out first (tile counts and the pool
            // follow it), and the turn passes on screen once it has landed — see
            // OnAfterRenderAsync. My own moves apply at once: the turn must leave me.
            _gameState = _gameState with
            {
                Board = state.Board,
                Players = state.Players,
                TilePoolCount = state.TilePoolCount
            };
            _turnAwaitingBoard = state;
        }
        else
        {
            ApplyTurn(state);
        }
    }

    private void ApplyIncomingHighlight(List<int>? indices, int displayMs, List<int>? modifiedSets)
    {
        // Reading-order indices of the tiles that entered the board (server-computed),
        // so the freshly-played copy lights up — never an identical twin already there.
        // Modified-set indices outline every combination the move touched. The server
        // sizes displayMs to the board animation plus a reading window; the highlight
        // then fades on its own instead of lingering until the next move.
        _highlightIndices = indices is { Count: > 0 } ? [..indices] : null;
        _modifiedSetIndices = modifiedSets is { Count: > 0 } ? [..modifiedSets] : null;
        // Remembered so a "replay animation" can bring the highlight back too.
        _lastHighlightIndices = _highlightIndices?.ToList();
        _lastModifiedSetIndices = _modifiedSetIndices?.ToList();
        _lastHighlightMs = displayMs;
        SetupHighlightTimer(displayMs);
    }

    private void ApplyRackAwaitingBoard()
    {
        if (_rackAwaitingBoard is null) return;
        _rackTiles = _rackAwaitingBoard;
        _rackAwaitingBoard = null;
    }

    // --- Turn display ---

    /// <summary>Shows a state in full: whose turn it is, the banner, my side of the table.</summary>
    private void ApplyTurn(GameStateDto state)
    {
        _gameState = state;
    }

    private void RevealTurn()
    {
        StopTurnRevealTimer();
        if (_turnAwaitingBoard is not { } state) return;
        _turnAwaitingBoard = null;
        ApplyTurn(state);
    }

    /// <summary>Reveals the held-back turn once the board stops moving.</summary>
    private void ScheduleTurnReveal(int boardBusyMs)
    {
        StopTurnRevealTimer();
        if (boardBusyMs <= 0)
        {
            RevealTurn();
            StateHasChanged();
            return;
        }

        var armedFor = _turnAwaitingBoard;
        _turnRevealTimer = new Timer(boardBusyMs) { AutoReset = false };
        _turnRevealTimer.Elapsed += (_, _) => InvokeAsync(() =>
        {
            // A late tick must not reveal a newer move whose board is still playing.
            if (!ReferenceEquals(armedFor, _turnAwaitingBoard)) return;
            RevealTurn();
            StateHasChanged();
        });
        _turnRevealTimer.Start();
    }

    private void StopTurnRevealTimer()
    {
        _turnRevealTimer?.Stop();
        _turnRevealTimer?.Dispose();
        _turnRevealTimer = null;
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
            var replayMs = await _tileAnimations.InvokeAsync<int>("replayLast");
            if (replayMs <= 0)
            {
                if (_canReplay)
                {
                    _canReplay = false;
                    StateHasChanged();
                }
                return;
            }

            // Keep the next move off this board until the story has landed again.
            ArmReplayHold(replayMs);

            if (_lastHighlightIndices is { Count: > 0 } || _lastModifiedSetIndices is { Count: > 0 })
            {
                _highlightIndices = _lastHighlightIndices is { Count: > 0 } ? [.._lastHighlightIndices] : null;
                _modifiedSetIndices = _lastModifiedSetIndices is { Count: > 0 } ? [.._lastModifiedSetIndices] : null;
                SetupHighlightTimer(_lastHighlightMs);
                StateHasChanged();
            }
        }
        catch (Exception e) when (e is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // Best-effort, like the rest of the animation layer.
        }
    }

    /// <summary>
    /// Freezes the board for <paramref name="replayMs"/> so an incoming move — the next AI
    /// turn, usually — cannot replace the story being replayed. Queued moves play afterwards.
    /// </summary>
    private void ArmReplayHold(int replayMs)
    {
        StopHeldDrainTimer();
        _releaseHeldAfterRender = false;
        _drainingHeld = false;
        var generation = ++_replayHoldGeneration;

        _replayHoldTimer?.Stop();
        _replayHoldTimer?.Dispose();
        _replayHoldTimer = new Timer(replayMs) { AutoReset = false };
        _replayHoldTimer.Elapsed += (_, _) => InvokeAsync(() =>
        {
            // A second click started a newer replay; this tick belongs to the old one.
            if (generation != _replayHoldGeneration) return;
            ReleaseReplayHold();
        });
        _replayHoldTimer.Start();
    }

    private void ReleaseReplayHold()
    {
        _replayHoldTimer?.Stop();
        _replayHoldTimer?.Dispose();
        _replayHoldTimer = null;

        if (_heldMoves.Count == 0) return;

        _drainingHeld = true;
        ApplyNextHeld();
        StateHasChanged();
    }

    private void ApplyNextHeld()
    {
        if (_heldMoves.Count == 0)
        {
            _drainingHeld = false;
            _releaseHeldAfterRender = false;
            return;
        }

        var move = _heldMoves[0];
        _heldMoves.RemoveAt(0);
        ApplyIncomingState(move.State);
        if (move.Highlighted)
            ApplyIncomingHighlight(move.Indices, move.DisplayMs, move.ModifiedSets);
        if (move.Optimal is not null)
            ApplyOptimal(move.Optimal);

        _drainingHeld = _heldMoves.Count > 0;
        _releaseHeldAfterRender = _heldMoves.Count > 0;
    }

    /// <summary>Applies the next move parked during a replay, once the board is idle.</summary>
    private void ScheduleHeldDrain(int boardBusyMs)
    {
        StopHeldDrainTimer();
        if (_heldMoves.Count == 0 || _replayHoldTimer is not null) return;

        if (boardBusyMs <= 0)
        {
            ApplyNextHeld();
            StateHasChanged();
            return;
        }

        _heldDrainTimer = new Timer(boardBusyMs) { AutoReset = false };
        _heldDrainTimer.Elapsed += (_, _) => InvokeAsync(() =>
        {
            if (_replayHoldTimer is not null) return;
            ApplyNextHeld();
            StateHasChanged();
        });
        _heldDrainTimer.Start();
    }

    private void StopHeldDrainTimer()
    {
        _heldDrainTimer?.Stop();
        _heldDrainTimer?.Dispose();
        _heldDrainTimer = null;
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

    /// <summary>Saved from this tab, or by anyone at the table (the list is shared).</summary>
    private bool SeedSaved => _seedSaved || _gameState is { SeedSaved: true };

    private string SeedChipTitle =>
        _gameState?.DistributionSeed
        ?? "This game's seed: Save seed adds it to the lobby list; its value shows here when the game ends.";

    private async Task OnSaveSeed()
    {
        if (_hubConnection is null || SeedSaved) return;

        try
        {
            await _hubConnection.SendAsync(HubEvents.SaveSeed, GameId);
        }
        catch (Exception ex)
        {
            _error = $"Error: {ex.Message}";
        }
    }

    private async Task OnCopySeed()
    {
        if (_gameState?.DistributionSeed is not { } seed) return;

        try
        {
            await JS.InvokeVoidAsync("navigator.clipboard.writeText", seed);
            _seedCopied = true;
        }
        catch (Exception ex)
        {
            _error = $"Could not copy the seed: {ex.Message}";
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

    /// <summary>
    /// Asks for this turn's optimal tiles and selects them in the play zone.
    /// A click before the solve answers waits for it; a later click replaces
    /// whatever was picked by hand. An empty answer leaves a hand-made selection
    /// alone and, when the zone is empty, says to draw.
    /// </summary>
    private void RequestHint()
    {
        if (!CanAct || _isLoading) return;
        _hintRequested = true;
        _hintNoMove = false;
        ApplyRequestedHint();
    }

    private void ApplyOptimal(List<Tile>? tiles)
    {
        _optimalReady = true;
        _optimalSuggestion = tiles is { Count: > 0 } ? tiles : null;
        // No-op unless Hint was clicked, and until this turn is on screen.
        ApplyRequestedHint();
    }

    private void ApplyRequestedHint()
    {
        if (!_hintRequested || !CanAct || !_optimalReady) return;

        _hintRequested = false;
        if (_optimalSuggestion is not { Count: > 0 })
        {
            _hintNoMove = true;
            return;
        }

        _hintNoMove = false;
        SelectedTilesForPlay.Clear();
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
        _drawnTile = null;

        // Each click selects one more copy; a click past the last copy clears them all.
        var totalAvailable = _rackTiles.Count(t => t.Equals(tileType));
        if (GetSelectedCountForTile(tileType) < totalAvailable)
            SelectedTilesForPlay.Add(tileType);
        else
            SelectedTilesForPlay.RemoveAll(t => t.Equals(tileType));

        StateHasChanged();
    }

    // Each selected tile with its copy number (0 for the first 7♦, 1 for the second…).
    private IEnumerable<(Tile Tile, int Copy)> SelectedTilesWithCopy()
    {
        var seen = new Dictionary<Tile, int>();
        foreach (var tile in SelectedTilesForPlay)
        {
            var copy = seen.GetValueOrDefault(tile);
            seen[tile] = copy + 1;
            yield return (tile, copy);
        }
    }

    private int GetSelectedCountForTile(Tile tileType) =>
        SelectedTilesForPlay.Count(t => t.Equals(tileType));

    // Jokers always come last; the player picks the order of the rest.
    private IEnumerable<IGrouping<Tile, Tile>> GetGroupedTiles()
    {
        var groups = _rackTiles.GroupBy(t => t).OrderBy(g => g.Key.IsJoker ? 1 : 0);
        return _rackSort == RackSort.Value
            ? groups.ThenBy(g => g.Key.Value).ThenBy(g => g.Key.Color)
            : groups.ThenBy(g => g.Key.Color).ThenBy(g => g.Key.Value);
    }

    // --- Rack sort preference (per browser, kept in localStorage) ---

    private enum RackSort { Color, Value }

    private const string RackSortStorageKey = "rummihub-rack-sort";

    private async Task LoadRackSortAsync()
    {
        try
        {
            var stored = await JS.InvokeAsync<string?>("localStorage.getItem", RackSortStorageKey);
            if (Enum.TryParse<RackSort>(stored, out var sort) && sort != _rackSort)
            {
                _rackSort = sort;
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // Storage blocked or circuit gone: keep the default order.
        }
    }

    private async Task SetRackSortAsync(RackSort sort)
    {
        if (sort == _rackSort) return;
        _rackSort = sort;
        try
        {
            await JS.InvokeVoidAsync("localStorage.setItem", RackSortStorageKey, sort.ToString());
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException)
        {
            // Not remembered for next time; this game still uses the new order.
        }
    }

    // --- Played-tiles highlight ---

    private void SetupHighlightTimer(int displayMs)
    {
        StopHighlightTimer();
        if (_highlightIndices is null && _modifiedSetIndices is null) return;

        _highlightTimer = new Timer(Math.Max(displayMs, 1000)) { AutoReset = false };
        _highlightTimer.Elapsed += (_, _) =>
        {
            InvokeAsync(() =>
            {
                _highlightIndices = null;
                _modifiedSetIndices = null;
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

    /// <summary>A move that arrived while a replay was on screen, applied once it lands.</summary>
    private sealed class HeldMove
    {
        public required GameStateDto State { get; init; }
        public bool Highlighted { get; set; }
        public List<int>? Indices { get; set; }
        public int DisplayMs { get; set; }
        public List<int>? ModifiedSets { get; set; }
        /// <summary>This turn's OptimalReady tiles (empty: no move); null until it arrives.</summary>
        public List<Tile>? Optimal { get; set; }
    }

    public async ValueTask DisposeAsync()
    {
        _highlightTimer?.Dispose();
        _turnRevealTimer?.Dispose();
        _replayHoldTimer?.Dispose();
        _heldDrainTimer?.Dispose();

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
