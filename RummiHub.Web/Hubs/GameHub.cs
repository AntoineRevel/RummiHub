using Microsoft.AspNetCore.SignalR;
using RummiHub.Web.Services;

namespace RummiHub.Web.Hubs;

public partial class GameHub(IGameManager gameManager, ILogger<GameHub> logger) : Hub
{
    public async Task CreateGame(string playerName)
    {
        try
        {
            playerName = playerName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Le nom du joueur est requis.");
                return;
            }

            var game = gameManager.CreateGame(playerName, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, game.Id);
            await Clients.Caller.SendAsync(HubEvents.GameCreated, game.Id, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(CreateGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error,
                "Une erreur est survenue lors de la creation de la partie.");
        }
    }

    public async Task JoinGame(string gameId, string playerName)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;
            playerName = playerName?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(gameId) || string.IsNullOrWhiteSpace(playerName))
            {
                await Clients.Caller.SendAsync(HubEvents.Error,
                    "Le code de la partie et le nom du joueur sont requis.");
                return;
            }

            var game = gameManager.GetGame(gameId);
            if (game == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Partie introuvable.");
                return;
            }

            if (game.HasPlayerWithName(playerName))
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Ce nom est deja pris dans cette partie.");
                return;
            }

            var player = gameManager.AddPlayer(gameId, playerName, Context.ConnectionId);
            if (player == null)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Impossible de rejoindre la partie.");
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
            await Clients.Group(gameId).SendAsync(HubEvents.PlayerJoined, player.Name, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(JoinGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "Une erreur est survenue en rejoignant la partie.");
        }
    }

    public async Task StartGame(string gameId)
    {
        try
        {
            gameId = gameId?.Trim() ?? string.Empty;

            var game = gameManager.GetGame(gameId);
            if (game == null || game.HostConnectionId != Context.ConnectionId)
                return;

            if (game.PlayerCount < 2)
            {
                await Clients.Caller.SendAsync(HubEvents.Error, "Il faut au moins 2 joueurs pour commencer.");
                return;
            }

            game.IsStarted = true;
            LogGameStarted(gameId);
            await Clients.Group(gameId).SendAsync(HubEvents.GameStarted, game.GetPlayerNames());
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(StartGame), Context.ConnectionId);
            await Clients.Caller.SendAsync(HubEvents.Error, "Une erreur est survenue lors du lancement de la partie.");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            var game = gameManager.FindGameByConnection(Context.ConnectionId);
            if (game != null)
            {
                var gameId = game.Id;
                gameManager.RemovePlayer(Context.ConnectionId);

                var updatedGame = gameManager.GetGame(gameId);
                if (updatedGame != null)
                    await Clients.Group(gameId).SendAsync(HubEvents.PlayerLeft, updatedGame.GetPlayerNames());
            }
        }
        catch (Exception ex)
        {
            LogHubError(ex, nameof(OnDisconnectedAsync), Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Game {GameId} started")]
    private partial void LogGameStarted(string gameId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in {MethodName} for connection {ConnectionId}")]
    private partial void LogHubError(Exception ex, string methodName, string connectionId);
}