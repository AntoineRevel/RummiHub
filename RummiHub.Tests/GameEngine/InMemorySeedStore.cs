using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

/// <summary><see cref="ISeedStore"/> in a list, same rules as <see cref="FileSeedStore"/>.</summary>
internal sealed class InMemorySeedStore : ISeedStore
{
    public List<SavedSeed> Seeds { get; } = [];

    public IReadOnlyList<SavedSeed> GetAll() => Seeds.ToList();

    public void Add(string seed)
    {
        Seeds.RemoveAll(s => s.Seed == seed);
        Seeds.Insert(0, new SavedSeed(seed, DateTimeOffset.UtcNow));
    }

    public void Remove(string seed) => Seeds.RemoveAll(s => s.Seed == seed);
}
