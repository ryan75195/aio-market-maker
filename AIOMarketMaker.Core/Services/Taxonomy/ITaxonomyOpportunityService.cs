namespace AIOMarketMaker.Core.Services.Taxonomy;

public interface ITaxonomyOpportunityService
{
    Task<int> Compute(int jobId, double feePercent, int minComps, CancellationToken ct = default);
}
