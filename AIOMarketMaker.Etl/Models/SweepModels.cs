namespace AIOMarketMaker.Etl.Models;

public record SweepOrchestratorInput(int ScrapeRunId);

public record StaleListingInfo(
    string ListingId,
    bool BlobExists,
    bool OrchestrationExists
);

public record FindStalePendingListingsResult(List<StaleListingInfo> StaleListings);

public record StartOrchestrationInput(int ScrapeRunId, string ListingId);
