namespace AIOMarketMaker.Core.Data.Models;

public class ScrapeRunIssue
{
    public int Id { get; set; }
    public int ScrapeRunId { get; set; }
    public string ListingId { get; set; } = string.Empty;
    public string IssueType { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string? Phase { get; set; }
    public string? StackTrace { get; set; }
    public int? HttpStatusCode { get; set; }
    public DateTime CreatedUtc { get; set; }

    public ScrapeRun? ScrapeRun { get; set; }
}
