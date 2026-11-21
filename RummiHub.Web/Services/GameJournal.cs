using System.Text.Json;
using System.Text.Json.Serialization;
using RummiHub.Web.Models;

namespace RummiHub.Web.Services;

/// <summary>What a <see cref="JournalEntry"/> records (<see cref="JournalEntry.Kind"/>).</summary>
public static class JournalKinds
{
    /// <summary>A host opened a game in the lobby.</summary>
    public const string Created = "created";

    /// <summary>A human took a seat in a game still in the lobby.</summary>
    public const string Joined = "joined";

    /// <summary>The host started the game: <see cref="JournalEntry.Seats"/> is the table.</summary>
    public const string Started = "started";

    /// <summary>A seat put tiles on the table (<see cref="JournalEntry.Tiles"/>, jokers included).</summary>
    public const string Played = "played";

    /// <summary>A seat drew a tile.</summary>
    public const string Drew = "drew";

    /// <summary>An AI seat had nothing to play and the pool was empty.</summary>
    public const string Passed = "passed";

    /// <summary>A seat emptied its rack: <see cref="JournalEntry.Seat"/> won.</summary>
    public const string Ended = "ended";

    /// <summary>The site owner said, on the admin page, that this visitor is them.</summary>
    public const string Mine = "mine";

    /// <summary>The site owner took that back.</summary>
    public const string NotMine = "not-mine";
}

/// <summary>One seat of a started game, in play order.</summary>
public sealed record JournalSeat(string Name, bool Ai, string? Visitor);

/// <summary>
/// One line of the game journal. Only the fields that <see cref="Kind"/> needs are set; the
/// others stay out of the file.
/// </summary>
public sealed record JournalEntry
{
    public DateTimeOffset At { get; init; }
    public required string Kind { get; init; }

    /// <summary>The game id (the code in the game URL).</summary>
    public string? Game { get; init; }

    /// <summary>The browser's anonymous id (<see cref="VisitorIds"/>), when the page sent one.</summary>
    public string? Visitor { get; init; }

    /// <summary>The name typed in the lobby.</summary>
    public string? Name { get; init; }

    /// <summary>The seat that moved, or the winner's.</summary>
    public int? Seat { get; init; }

    public int? Tiles { get; init; }
    public List<JournalSeat>? Seats { get; init; }

    /// <summary>Where the deal comes from: <c>random</c>, <c>pasted</c> or <c>list</c>.</summary>
    public string? Seed { get; init; }

    public bool? NoJokers { get; init; }

    /// <summary>Rounds played when the game ended (<see cref="GameEngine.Turn"/>).</summary>
    public int? Turns { get; init; }

    /// <summary>Tiles left on each rack when the game ended, by seat.</summary>
    public List<int>? TilesLeft { get; init; }
}

/// <summary>
/// Every game played, persisted for the statistics page (<c>/admin/statistiques</c>). The server
/// writes it, so it does not miss a game whose page was closed early or blocked the tracker.
/// </summary>
public interface IGameJournal
{
    /// <summary>Appends the entry, dated now. Never throws: the journal must not break a game.</summary>
    void Record(JournalEntry entry);

    /// <summary>Every entry, oldest first.</summary>
    IReadOnlyList<JournalEntry> ReadAll();
}

/// <summary>
/// <see cref="IGameJournal"/> as a JSON Lines file (<see cref="GameOptions.JournalFile"/>), one
/// entry per line. Production and the test site each have their own (separate volumes): test
/// games never count. A line that does not parse is skipped.
/// </summary>
public sealed partial class FileGameJournal(string path, TimeProvider time, ILogger<FileGameJournal> logger)
    : IGameJournal
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Lock _gate = new();

    public void Record(JournalEntry entry)
    {
        var line = JsonSerializer.Serialize(entry with { At = time.GetUtcNow() }, JsonOptions);
        try
        {
            lock (_gate)
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(ex, path);
        }
    }

    public IReadOnlyList<JournalEntry> ReadAll()
    {
        string[] lines;
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path)) return [];
                lines = File.ReadAllLines(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(ex, path);
            return [];
        }

        var entries = new List<JournalEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions) is { } entry)
                    entries.Add(entry);
            }
            catch (JsonException)
            {
                // A line cut by a crash mid-write: the rest of the journal still counts.
            }
        }

        return entries;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not append to the game journal {Path}")]
    private partial void LogWriteFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the game journal {Path}")]
    private partial void LogReadFailed(Exception ex, string path);
}

/// <summary>The journal entries a game produces, built from its state.</summary>
public static class GameJournalExtensions
{
    /// <summary>
    /// Records a move by <paramref name="seat"/>: tiles put down (<paramref name="tiles"/> &gt; 0),
    /// a draw, or a pass. When the move emptied the rack, also records the end of the game.
    /// </summary>
    public static void RecordMove(this IGameJournal journal, Game game, int seat, int tiles, bool drew)
    {
        var kind = tiles > 0 ? JournalKinds.Played : drew ? JournalKinds.Drew : JournalKinds.Passed;
        journal.Record(new JournalEntry
        {
            Kind = kind,
            Game = game.Id,
            Seat = seat,
            Tiles = tiles > 0 ? tiles : null
        });

        if (game.Engine is not { IsGameOver: true } engine) return;

        journal.Record(new JournalEntry
        {
            Kind = JournalKinds.Ended,
            Game = game.Id,
            Seat = seat,
            Turns = engine.Turn,
            TilesLeft = engine.Players.Select(p => p.Rack.Tiles.Count + p.Rack.Jokers).ToList()
        });
    }
}

/// <summary>
/// The anonymous id each browser keeps in the <see cref="CookieName"/> cookie (wwwroot/js/stats.js).
/// It tells players apart across games, days and networks; it carries nothing about the person.
/// </summary>
public static class VisitorIds
{
    public const string CookieName = "rummi-visiteur";

    /// <summary>The id when it has the shape stats.js makes (8 to 64 letters, digits, '-' or '_'), else null.</summary>
    public static string? Normalize(string? id)
    {
        id = id?.Trim();
        if (id is null || id.Length is < 8 or > 64) return null;
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;
    }
}
