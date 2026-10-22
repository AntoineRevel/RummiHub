namespace RummiHub.Web.Models;

/// <summary>Table limits shared by the hub, the lobby manager, and the dealer.</summary>
public static class GameLimits
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;

    public static string TooManyPlayersMessage =>
        $"A game can have at most {MaxPlayers} players.";
}
