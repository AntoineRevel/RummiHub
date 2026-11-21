using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

/// <summary>The game journal kept in a list, dated by the caller's clock (now by default).</summary>
internal sealed class InMemoryGameJournal : IGameJournal
{
    public List<JournalEntry> Entries { get; } = [];

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    public void Record(JournalEntry entry) => Entries.Add(entry with { At = Clock() });

    public IReadOnlyList<JournalEntry> ReadAll() => Entries;
}
