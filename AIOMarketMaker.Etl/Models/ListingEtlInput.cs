namespace AIOMarketMaker.Etl.Models;

public enum TriggerSource
{
    Listing,
    Description
}

public record ListingEtlInput(
    int ScrapeRunId,
    string ListingId,
    TriggerSource TriggerSource
);

public record BlobState(
    bool HasListing,
    bool HasDescription,
    string? MissingBlob
)
{
    public bool HasBoth => HasListing && HasDescription;
}

public record ProcessListingInput(
    string ListingId,
    int ScrapeJobId,
    int ScrapeRunId,
    bool HasDescription
);

public record ProcessListingResult(
    bool Success,
    bool IsNewListing = false,
    string? ErrorMessage = null
);

// Activity DTOs for orchestration
public record ScrapeJobInfo(int Id, string SearchTerm);
public record SearchPageResult(bool Success, List<string> ListingIds, string? Error);
public record ParseSearchPageInput(string Html, int Page, bool IsSold, int? LookbackDays);
public record FilterNewListingsInput(int JobId, List<string> ListingIds);

public record UpdateScrapeRunInput(
    string InstanceId,
    bool Success,
    int ListingsAdded,
    int ListingsSkipped,
    string? ErrorMessage);

public record UpdateProgressInput(
    string InstanceId,
    int? TotalListingsFound = null,
    int? ListingsProcessed = null,
    string? CurrentPhase = null);

// Orchestrator DTOs
public record ScrapeOrchestratorInput(int ScrapeRunId, int? MaxSoldListings, int? MaxActiveListings, int? LookbackDays);
public record OrchestratorResult(
    int SucceededJobs,
    int FailedJobs,
    int TotalListingsFound,
    TimeSpan Duration,
    List<string> Errors);

// Job orchestration DTOs
public record JobDetails(int Id, string SearchTerm, int LookbackDays, int? MaxSoldListings, int? MaxActiveListings);
public record JobResult(int JobId, bool Success, int ListingsFound, string? Error);
public record JobOrchestratorInput(int JobId, string ScrapeInstanceId, int? MaxSoldListings = null, int? MaxActiveListings = null, int? LookbackDays = null);
public record GetJobDetailsInput(int JobId, int? MaxSoldListings = null, int? MaxActiveListings = null, int? LookbackDays = null);

// URL building DTOs
public record BuildSearchUrlInput(string SearchTerm, bool IsSold, int Page);
public record FetchListingInput(string ListingId, string ListingUrl);

// Listing DTOs
public record ListingData(
    string ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Condition,
    string? ListingStatus,
    string? PurchaseFormat,
    string? Description,
    string? Url,
    DateTime? EndDateUtc,
    string? Location,
    string? ItemSpecifics,
    List<string>? Images
);
public record SaveListingsInput(int JobId, List<ListingData> Listings);

// Active listings DTOs (for sold detection)
public record ActiveListingInfo(int Id, string ListingId);
public record GetActiveListingsInput(int JobId);
public record UpdateSoldListingsInput(int JobId, List<ListingData> SoldListings);

// Scrape job status DTOs
public record ScrapeJobStatusResult(string JobId, string Status, bool IsComplete);
public record GetScrapedHtmlInput(string JobId, string? GroupId = null, string? FileKey = null);

// Scrape submission DTOs
public record SubmitScrapeJobInput(
    string Url,
    string? GroupId = null,
    string? FileKey = null
);

// Sub-orchestrator input for ScrapeUrlOrchestrator
public record ScrapeUrlInput(
    string Url,
    string? GroupId = null,
    string? FileKey = null
);

// Batch scrape job submission DTOs
public record SubmitScrapeJobsInput(int ScrapeRunId, List<string> ListingIds);
public record SubmitScrapeJobsResult(int SubmittedCount, int FailedCount);

// Parsing DTOs
public record ParseListingInput(string ListingId, string ListingUrl, string Html);
public record ParsedListingResult(
    string ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Condition,
    string? ListingStatus,
    string? PurchaseFormat,
    string? Url,
    DateTime? EndDateUtc,
    string? Location,
    string? ItemSpecifics,
    List<string>? Images,
    string? DescriptionSourceUrl
);
