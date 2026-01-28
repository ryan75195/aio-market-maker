namespace AIOMarketMaker.Etl.Models;

/// <summary>
/// Input for EnqueueScrapeRetryActivity.
/// </summary>
/// <param name="ListingId">The eBay listing ID</param>
/// <param name="FileKey">Which blob to retry: "listing" or "description"</param>
public record EnqueueScrapeRetryInput(string ListingId, string FileKey);
