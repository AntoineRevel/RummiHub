namespace RummiHub.Web.Hubs;

public static class HubEvents
{
    // Server -> Client
    public const string GameCreated = nameof(GameCreated);
    public const string PlayerJoined = nameof(PlayerJoined);
    public const string PlayerLeft = nameof(PlayerLeft);
    public const string GameStarted = nameof(GameStarted);
    public const string Error = nameof(Error);

    // Client -> Server
    public const string CreateGame = nameof(CreateGame);
    public const string JoinGame = nameof(JoinGame);
    public const string StartGame = nameof(StartGame);
}