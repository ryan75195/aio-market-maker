namespace AIOMarketMaker.Etl.Models;

/// <summary>
/// Input for EnqueueScrapeRetryActivity.
/// </summary>
/// <param name="ListingId">The eBay listing ID</param>
/// <param name="FileKey">Which blob to retry: "listing" or "description"</param>
/// <param name="ScrapeRunId">Optional scrape run ID for blob path scoping</param>
public record EnqueueScrapeRetryInput(string ListingId, string FileKey, int? ScrapeRunId = null);
