namespace RummiHub.Web.Services;

public class GameOptions
{
    /// <summary>
    /// Seeds written into a brand-new catalog file. Once that file exists it is the only
    /// list: later startups do not merge these ids back in.
    /// </summary>
    public List<string> PredefinedIds { get; set; } = [];
}
