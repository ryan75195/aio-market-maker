using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

// NOTE: This service will be replaced in Task 4 (ComparablesEtlService).
// Stubbed to allow compilation after ListingPricingComparable removal.

public interface IComparablesRefreshService
{
    Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default);
}

public record ComparablesRefreshResult(int ListingsProcessed, int ComparablesFound);

public class ComparablesRefreshService : IComparablesRefreshService
{
    public Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings,
        CancellationToken ct = default)
    {
        throw new NotImplementedException("ComparablesRefreshService will be replaced by ComparablesEtlService in Task 4");
    }
}

public class NullComparablesRefreshService : IComparablesRefreshService
{
    public Task<ComparablesRefreshResult> Refresh(
        IEnumerable<Listing> activeListings, CancellationToken ct = default)
        => Task.FromResult(new ComparablesRefreshResult(0, 0));
}
