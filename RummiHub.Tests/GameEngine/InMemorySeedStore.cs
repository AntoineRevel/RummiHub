using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

/// <summary><see cref="ISeedStore"/> in a list, same rules as <see cref="FileSeedStore"/>.</summary>
internal sealed class InMemorySeedStore : ISeedStore
{
    public List<SavedSeed> Seeds { get; } = [];

    public IReadOnlyList<SavedSeed> GetAll() => Seeds.ToList();

    public void Add(string seed, int players, int seat, string? name)
    {
        if (!SavedSeed.IsValid(seed, players, seat))
            return;

        var entry = new SavedSeed(seed, players, seat, SavedSeed.NormalizeName(name), DateTimeOffset.UtcNow);
        var previous = Seeds.FirstOrDefault(s => s.ListId == entry.ListId);
        Seeds.RemoveAll(s => s.ListId == entry.ListId);
        Seeds.Insert(0, entry with { Name = entry.Name ?? previous?.Name });
    }

    public void Rename(string listId, string? name)
    {
        var index = Seeds.FindIndex(s => s.ListId == listId);
        if (index >= 0)
            Seeds[index] = Seeds[index] with { Name = SavedSeed.NormalizeName(name) };
    }

    public void Remove(string listId) => Seeds.RemoveAll(s => s.ListId == listId);
}
