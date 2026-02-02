namespace AIOMarketMaker.Core.Data.Models;

/// <summary>
/// Represents a single execution of the scrape orchestration.
/// Tracks when it ran and how many listings were added.
/// </summary>
public class ScrapeRun
{
    public int Id { get; set; }

    /// <summary>
    /// The Durable Functions orchestration instance ID
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// How the run was triggered: Manual, Nightly
    /// </summary>
    public string TriggerType { get; set; } = "Manual";

    /// <summary>
    /// The scrape job this run is for (null for legacy runs that processed all jobs)
    /// </summary>
    public int? JobId { get; set; }

    /// <summary>
    /// When the scrape run started
    /// </summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// When the scrape run completed (null if still running or failed)
    /// </summary>
    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// Status of the run: Running, Completed, Failed
    /// </summary>
    public string Status { get; set; } = "Running";

    /// <summary>
    /// Number of new active listings added to the database
    /// </summary>
    public int ListingsAddedActive { get; set; }

    /// <summary>
    /// Number of new sold listings added to the database
    /// </summary>
    public int ListingsAddedSold { get; set; }

    /// <summary>
    /// Number of listings that were duplicates (already in DB)
    /// </summary>
    public int ListingsSkipped { get; set; }

    /// <summary>
    /// Number of listings that failed processing (error pages, parse failures)
    /// </summary>
    public int ListingsFailed { get; set; }

    /// <summary>
    /// Number of existing listings that were updated (re-scraped with new data)
    /// </summary>
    public int ListingsUpdated { get; set; }

    /// <summary>
    /// Error message if the run failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Total unique listings found during search phase
    /// </summary>
    public int TotalListingsFound { get; set; }

    /// <summary>
    /// Number of listings fully processed (fetched and saved)
    /// </summary>
    public int ListingsProcessed { get; set; }

    /// <summary>
    /// Current phase of the orchestration: "Searching Sold", "Searching Active", "Filtering", "Fetching", "Saving", "Completed"
    /// </summary>
    public string? CurrentPhase { get; set; }
}
