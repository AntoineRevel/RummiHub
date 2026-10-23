using System.Diagnostics;
using OpenTelemetry;

namespace RummiHub.Solver.Worker;

/// <summary>
/// N'exporte pas les commandes avec lesquelles <see cref="SolverWorker"/> attend du travail :
/// XREADGROUP et XAUTOCLAIM, quatre par réveil (deux streams), au moins toutes les 30 s par
/// worker, presque toujours vides. Lancées hors de toute requête, chacune formait sa propre
/// trace : elles noyaient les résolutions et remplissaient la mémoire du tableau de bord.
/// </summary>
public sealed class RedisPollingFilter : BaseProcessor<Activity>
{
    /// <summary>
    /// Source de l'instrumentation Redis qu'Aspire.StackExchange.Redis embarque. Le paquet la
    /// garde interne, son option Filter comprise, d'où ce processeur.
    /// </summary>
    public const string RedisSourceName = "OpenTelemetry.Instrumentation.StackExchangeRedis";

    public override void OnStart(Activity activity)
    {
        if (activity.Source.Name != RedisSourceName
            || activity.OperationName is not ("XREADGROUP" or "XAUTOCLAIM"))
            return;

        // Décidé dès le début de la span : aucun processeur ne l'exporte à la fin, quel que
        // soit l'ordre dans lequel ils sont enregistrés.
        activity.IsAllDataRequested = false;
        activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
    }
}
