namespace RummiHub.Web.Models;

/// <summary>Table limits shared by the hub, the lobby manager, and the dealer.</summary>
public static class GameLimits
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;

    public static string TooManyPlayersMessage =>
        $"A game can have at most {MaxPlayers} players.";

    /// <summary>Solver-backed play attempts one seat may make per <see cref="PlayAttemptWindow"/>.</summary>
    public const int PlayAttemptsPerWindow = 10;

    public static readonly TimeSpan PlayAttemptWindow = TimeSpan.FromSeconds(10);

    public const string TooManyPlayAttemptsMessage =
        "Too many attempts — wait a few seconds before trying again.";
}
