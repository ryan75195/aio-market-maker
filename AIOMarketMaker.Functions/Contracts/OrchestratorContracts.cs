namespace AIOMarketMaker.Functions.Contracts;

// API request DTOs
public record StartScrapeRequest(int? MaxListingsToFetch, int? LookbackDays);

// Orchestrator input DTOs
public record ScrapeOrchestratorInput(int? MaxListingsToFetch, int? LookbackDays);

// Job orchestration DTOs
public record JobDetails(int Id, string SearchTerm, int LookbackDays, int? MaxListingsToFetch);
public record JobResult(int JobId, bool Success, int ListingsFound, string? Error);
public record ScrapeJobInfo(int Id, string SearchTerm);
public record JobOrchestratorInput(int JobId, string ScrapeInstanceId, int? MaxListingsToFetch = null, int? LookbackDays = null);
public record GetJobDetailsInput(int JobId, int? MaxListingsToFetch = null, int? LookbackDays = null);

// Search DTOs
public record SearchPageResult(bool Success, List<string> ListingIds, string? Error);
public record BuildSearchUrlInput(string SearchTerm, bool IsSold, int Page);
public record ParseSearchPageInput(string Html, int Page, bool IsSold, int? LookbackDays);

// Listing DTOs
public record FetchListingInput(string ListingId, string ListingUrl);
public record FilterNewListingsInput(int JobId, List<string> ListingIds);
public record SaveListingsInput(int JobId, List<ListingData> Listings);

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

// Orchestrator result DTOs
public record OrchestratorResult(
    int SucceededJobs,
    int FailedJobs,
    int TotalListingsFound,
    TimeSpan Duration,
    List<string> Errors);

// Active listings DTOs (for sold detection)
public record ActiveListingInfo(int Id, string ListingId);
public record GetActiveListingsInput(int JobId);
public record UpdateSoldListingsInput(int JobId, List<ListingData> SoldListings);
