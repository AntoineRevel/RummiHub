using System.Text.Json;
using System.Text.Json.Nodes;

namespace RummiHub.Web.Services;

/// <summary>
/// Persists saved distribution seeds so they survive a restart. Seeds are written "in hard"
/// into the <c>GameOptions:PredefinedIds</c> array of appsettings.json.
/// </summary>
public interface ISeedStore
{
    /// <summary>Adds the seed to the persisted predefined-id list. No-op if blank or already present.</summary>
    void Persist(string seed);
}

/// <summary>
/// <see cref="ISeedStore"/> backed by the appsettings.json file at the content root. Writes are
/// serialized through a lock and failures are logged rather than thrown — persistence is
/// best-effort and must never break the in-game "save seed" action.
/// </summary>
public sealed partial class AppSettingsSeedStore(IHostEnvironment environment, ILogger<AppSettingsSeedStore> logger)
    : ISeedStore
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(environment.ContentRootPath, "appsettings.json");

    public void Persist(string seed)
    {
        seed = seed?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(seed))
            return;

        lock (_gate)
        {
            try
            {
                var root = (File.Exists(_path)
                    ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
                    : null) ?? new JsonObject();

                if (root["GameOptions"] is not JsonObject gameOptions)
                {
                    gameOptions = new JsonObject();
                    root["GameOptions"] = gameOptions;
                }

                if (gameOptions["PredefinedIds"] is not JsonArray ids)
                {
                    ids = new JsonArray();
                    gameOptions["PredefinedIds"] = ids;
                }

                if (ids.Any(n => string.Equals(n?.GetValue<string>(), seed, StringComparison.Ordinal)))
                    return;

                ids.Add(seed);

                File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                LogSeedPersisted(seed, _path);
            }
            catch (Exception ex)
            {
                LogSeedPersistFailed(ex, seed, _path);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Seed {Seed} persisted to {Path}")]
    private partial void LogSeedPersisted(string seed, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to persist seed {Seed} to {Path}")]
    private partial void LogSeedPersistFailed(Exception ex, string seed, string path);
}
