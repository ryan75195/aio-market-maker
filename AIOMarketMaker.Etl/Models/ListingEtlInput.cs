namespace AIOMarketMaker.Etl.Models;

public enum TriggerSource
{
    Listing,
    Description
}

public record ListingEtlInput(
    string JobId,
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
    string JobId,
    string ListingId,
    int ScrapeJobId,
    bool HasDescription
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
