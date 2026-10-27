namespace RummiHub.Web.Services;

public class GameOptions
{
    /// <summary>
    /// The lobby seed list (<see cref="FileSeedStore"/>), relative to the content root.
    /// The container image points it at a volume shared by production and the test site.
    /// </summary>
    public string SeedsFile { get; set; } = "App_Data/seeds.json";
}
