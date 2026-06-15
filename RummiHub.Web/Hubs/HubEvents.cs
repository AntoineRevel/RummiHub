namespace RummiHub.Web.Hubs;

public static class HubEvents
{
    // === Lobby phase: Server -> Client ===
    public const string GameCreated = nameof(GameCreated);
    public const string PlayerJoined = nameof(PlayerJoined);
    public const string PlayerLeft = nameof(PlayerLeft);
    public const string GameStarted = nameof(GameStarted);
    public const string AvailableGameIds = nameof(AvailableGameIds);
    public const string Error = nameof(Error);

    // === Lobby phase: Client -> Server ===
    public const string CreateGame = nameof(CreateGame);
    public const string JoinGame = nameof(JoinGame);
    public const string AddAiPlayer = nameof(AddAiPlayer);
    public const string StartGame = nameof(StartGame);
    public const string GetAvailableGameIds = nameof(GetAvailableGameIds);

    // === Game phase: Server -> Client ===
    public const string GameStateUpdated = nameof(GameStateUpdated);
    public const string RackUpdated = nameof(RackUpdated);
    public const string TileDrawn = nameof(TileDrawn);
    public const string InvalidPlay = nameof(InvalidPlay);
    public const string HighlightTilesUpdated = nameof(HighlightTilesUpdated);
    public const string OptimalPlay = nameof(OptimalPlay);
    public const string OptimalReady = nameof(OptimalReady);

    // === Game phase: Client -> Server ===
    public const string JoinGameSession = nameof(JoinGameSession);
    public const string PlayTiles = nameof(PlayTiles);
    public const string DrawTile = nameof(DrawTile);
}
