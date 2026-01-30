namespace AIOMarketMaker.Etl.Models;

public record SweepOrchestratorInput(int ScrapeRunId);

public record PendingListingInfo(
    string ListingId,
    bool BlobExists
);

public record FindPendingListingsResult(List<PendingListingInfo> PendingListings);

public record StartOrchestrationInput(int ScrapeRunId, string ListingId);

public record StartSweepInput(int ScrapeRunId, string InstanceId);
