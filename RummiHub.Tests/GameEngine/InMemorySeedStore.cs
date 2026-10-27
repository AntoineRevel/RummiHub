using RummiHub.Web.Services;

namespace RummiHub.Tests.GameEngineTests;

/// <summary><see cref="ISeedStore"/> in a list, same rules as <see cref="FileSeedStore"/>.</summary>
internal sealed class InMemorySeedStore : ISeedStore
{
    public List<SavedSeed> Seeds { get; } = [];

    public IReadOnlyList<SavedSeed> GetAll() => Seeds.ToList();

    public void Add(string seed, DateTimeOffset? hiddenUntil = null)
    {
        var old = Seeds.FirstOrDefault(s => s.Seed == seed);
        if (old is not null)
        {
            Seeds.Remove(old);
            if (old.HiddenUntil is null) hiddenUntil = null;
        }

        Seeds.Insert(0, new SavedSeed(seed, DateTimeOffset.UtcNow, hiddenUntil));
    }

    public void Reveal(string seed)
    {
        var i = Seeds.FindIndex(s => s.Seed == seed);
        if (i >= 0) Seeds[i] = Seeds[i] with { HiddenUntil = null };
    }
}
