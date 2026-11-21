namespace RummiHub.Web.Services;

/// <summary>Where a game stands, seen from the journal.</summary>
public enum GameOutcome
{
    /// <summary>Created, not started, still active.</summary>
    Lobby,

    /// <summary>Created, never started, quiet for <see cref="GameStatistics.IdleAfter"/>.</summary>
    NeverStarted,

    /// <summary>Started, not over, a move within <see cref="GameStatistics.IdleAfter"/>.</summary>
    InProgress,

    /// <summary>A seat emptied its rack.</summary>
    Finished,

    /// <summary>Started, not over, no move for <see cref="GameStatistics.IdleAfter"/> (tab closed, server restarted).</summary>
    Abandoned
}

/// <summary>One seat of a game: in the lobby, the humans seated so far; once started, the table.</summary>
public sealed class SeatRecord
{
    public required string Name { get; init; }
    public bool Ai { get; init; }
    public string? Visitor { get; init; }

    /// <summary>Who sat there: the visitor id, or, without one, a key of that game and name.</summary>
    public required string PlayerKey { get; init; }

    public int Plays { get; set; }
    public int TilesPlayed { get; set; }
    public int Draws { get; set; }
}

public sealed class GameRecord
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset LastActivity { get; set; }

    /// <summary><c>random</c>, <c>pasted</c> or <c>list</c>; null when the creation is not in the journal.</summary>
    public string? SeedKind { get; set; }

    public bool NoJokers { get; set; }
    public List<SeatRecord> Seats { get; set; } = [];
    public int Moves { get; set; }
    public int? WinnerSeat { get; set; }
    public int? Turns { get; set; }
    public List<int>? TilesLeft { get; set; }
    public GameOutcome Outcome { get; set; }

    public bool Started => StartedAt is not null;
    public int Humans => Seats.Count(s => !s.Ai);
    public int Ais => Seats.Count(s => s.Ai);
    public SeatRecord? Winner => WinnerSeat is { } w && w < Seats.Count ? Seats[w] : null;
    public TimeSpan? Duration => EndedAt - StartedAt;
}

public sealed class PlayerRecord
{
    public required string Key { get; init; }

    /// <summary>False when the page sent no visitor id: this "player" is one seat of one game.</summary>
    public bool HasVisitorId { get; init; }

    public bool IsOwner { get; set; }
    public List<string> Names { get; } = [];
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.MaxValue;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
    public HashSet<DateOnly> Days { get; } = [];

    /// <summary>Games opened in the lobby, started or not.</summary>
    public int Created { get; set; }

    /// <summary>Lobbies of someone else joined.</summary>
    public int Joined { get; set; }

    /// <summary>Started games this player had a seat in.</summary>
    public int Played { get; set; }

    public int Finished { get; set; }
    public int Won { get; set; }
    public int Abandoned { get; set; }

    /// <summary>Started games with at least one other human at the table.</summary>
    public int WithOtherHumans { get; set; }

    public int TilesPlayed { get; set; }
    public int Draws { get; set; }

    internal void Saw(DateTimeOffset at, string? name)
    {
        if (at < FirstSeen) FirstSeen = at;
        if (at > LastSeen) LastSeen = at;
        Days.Add(DateOnly.FromDateTime(at.UtcDateTime));
        if (!string.IsNullOrWhiteSpace(name) && !Names.Contains(name)) Names.Add(name);
    }
}

public sealed class StatsReport
{
    /// <summary>The first journal entry; null for an empty journal.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Most recently created first.</summary>
    public required IReadOnlyList<GameRecord> Games { get; init; }

    /// <summary>Most recently seen first.</summary>
    public required IReadOnlyList<PlayerRecord> Players { get; init; }

    /// <summary>The visitor ids the site owner marked as theirs.</summary>
    public required IReadOnlySet<string> Owners { get; init; }

    /// <summary>A human other than the owner sat at this game (a seat without visitor id counts as another).</summary>
    public bool HasOthers(GameRecord game) => game.Seats.Any(s => !s.Ai && !Owners.Contains(s.PlayerKey));
}

/// <summary>Builds the statistics page's figures from the game journal.</summary>
public static class GameStatistics
{
    /// <summary>A game with no entry for this long is no longer in progress (or in the lobby).</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromHours(1);

    public static StatsReport Build(IReadOnlyList<JournalEntry> entries, DateTimeOffset now)
    {
        var games = new Dictionary<string, GameRecord>();
        var owners = new HashSet<string>();
        var hosts = new List<(GameRecord Game, SeatRecord Seat)>();
        var joins = new List<(GameRecord Game, SeatRecord Seat)>();

        GameRecord GameOf(string id, DateTimeOffset at)
        {
            if (!games.TryGetValue(id, out var game))
                games[id] = game = new GameRecord { Id = id, CreatedAt = at };
            if (at > game.LastActivity) game.LastActivity = at;
            return game;
        }

        foreach (var e in entries.OrderBy(e => e.At))
        {
            switch (e.Kind)
            {
                case JournalKinds.Mine when e.Visitor is not null:
                    owners.Add(e.Visitor);
                    continue;
                case JournalKinds.NotMine when e.Visitor is not null:
                    owners.Remove(e.Visitor);
                    continue;
            }

            if (e.Game is null) continue;
            var game = GameOf(e.Game, e.At);
            switch (e.Kind)
            {
                case JournalKinds.Created:
                    game.CreatedAt = e.At;
                    game.SeedKind = e.Seed;
                    game.NoJokers = e.NoJokers == true;
                    var host = Seat(e.Game, e.Name, ai: false, e.Visitor);
                    game.Seats.Add(host);
                    hosts.Add((game, host));
                    break;
                case JournalKinds.Joined when !game.Started:
                    var joined = Seat(e.Game, e.Name, ai: false, e.Visitor);
                    game.Seats.Add(joined);
                    joins.Add((game, joined));
                    break;
                case JournalKinds.Started when e.Seats is not null:
                    game.StartedAt = e.At;
                    game.Seats = e.Seats.Select(s => Seat(e.Game, s.Name, s.Ai, s.Visitor)).ToList();
                    break;
                case JournalKinds.Played or JournalKinds.Drew or JournalKinds.Passed:
                    game.Moves++;
                    if (e.Seat is { } seat && seat >= 0 && seat < game.Seats.Count)
                    {
                        var s = game.Seats[seat];
                        if (e.Kind == JournalKinds.Played)
                        {
                            s.Plays++;
                            s.TilesPlayed += e.Tiles ?? 0;
                        }
                        else if (e.Kind == JournalKinds.Drew)
                        {
                            s.Draws++;
                        }
                    }

                    break;
                case JournalKinds.Ended:
                    game.EndedAt = e.At;
                    game.WinnerSeat = e.Seat;
                    game.Turns = e.Turns;
                    game.TilesLeft = e.TilesLeft;
                    break;
            }
        }

        foreach (var game in games.Values)
            game.Outcome = OutcomeOf(game, now);

        var players = new Dictionary<string, PlayerRecord>();
        PlayerRecord PlayerOf(SeatRecord seat)
        {
            if (!players.TryGetValue(seat.PlayerKey, out var p))
                players[seat.PlayerKey] = p = new PlayerRecord
                {
                    Key = seat.PlayerKey,
                    HasVisitorId = seat.Visitor is not null
                };
            return p;
        }

        foreach (var game in games.Values)
        {
            foreach (var seat in game.Seats.Where(s => !s.Ai))
            {
                var p = PlayerOf(seat);
                p.Saw(game.CreatedAt, seat.Name);
                p.Saw(game.LastActivity, null);
                if (!game.Started) continue;

                p.Played++;
                if (game.Humans > 1) p.WithOtherHumans++;
                if (game.Outcome == GameOutcome.Finished) p.Finished++;
                if (game.Outcome == GameOutcome.Abandoned) p.Abandoned++;
                if (ReferenceEquals(game.Winner, seat)) p.Won++;
                p.TilesPlayed += seat.TilesPlayed;
                p.Draws += seat.Draws;
            }
        }

        // Hosting and joining count even when the player left the lobby before the start.
        foreach (var (game, seat) in hosts)
        {
            var p = PlayerOf(seat);
            p.Saw(game.CreatedAt, seat.Name);
            p.Created++;
        }

        foreach (var (game, seat) in joins)
        {
            var p = PlayerOf(seat);
            p.Saw(game.CreatedAt, seat.Name);
            p.Joined++;
        }

        foreach (var p in players.Values)
            p.IsOwner = owners.Contains(p.Key);

        return new StatsReport
        {
            Since = entries.Count == 0 ? null : entries.Min(e => e.At),
            Games = games.Values.OrderByDescending(g => g.CreatedAt).ToList(),
            Players = players.Values.OrderByDescending(p => p.LastSeen).ToList(),
            Owners = owners
        };
    }

    private static SeatRecord Seat(string gameId, string? name, bool ai, string? visitor) => new()
    {
        Name = name ?? "?",
        Ai = ai,
        Visitor = visitor,
        PlayerKey = ai ? $"ai:{gameId}:{name}" : visitor ?? $"?{gameId}:{name}"
    };

    private static GameOutcome OutcomeOf(GameRecord game, DateTimeOffset now)
    {
        var idle = now - game.LastActivity >= IdleAfter;
        if (game.EndedAt is not null) return GameOutcome.Finished;
        if (game.Started) return idle ? GameOutcome.Abandoned : GameOutcome.InProgress;
        return idle ? GameOutcome.NeverStarted : GameOutcome.Lobby;
    }
}
