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
