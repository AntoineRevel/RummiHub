namespace RummiHub.Web.Services;

public class GameOptions
{
    /// <summary>
    /// Seeds the lobby offered before the shared list: they start the list while
    /// <see cref="SeedsFile"/> does not exist yet.
    /// </summary>
    public List<string> PredefinedIds { get; set; } = [];

    /// <summary>
    /// The lobby seed list (<see cref="FileSeedStore"/>), relative to the content root.
    /// The container image points it at a volume shared by production and the test site.
    /// </summary>
    public string SeedsFile { get; set; } = "App_Data/seeds.json";
}
