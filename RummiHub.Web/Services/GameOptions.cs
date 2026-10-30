namespace RummiHub.Web.Services;

public class GameOptions
{
    /// <summary>
    /// The lobby seed list (<see cref="FileSeedStore"/>), relative to the content root.
    /// The container image points it at a volume shared by production and the test site.
    /// Entries carry the table size and seat since the file became seed-configs.json; the
    /// older seeds.json (seeds alone) is no longer read.
    /// </summary>
    public string SeedsFile { get; set; } = "App_Data/seed-configs.json";
}
