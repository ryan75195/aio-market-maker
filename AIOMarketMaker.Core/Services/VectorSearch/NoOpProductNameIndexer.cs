using AIOMarketMaker.Core.Models;

namespace AIOMarketMaker.Core.Services.VectorSearch;

/// <summary>
/// No-op product name indexer used when Pinecone is not configured.
/// </summary>
public class NoOpProductNameIndexer : IProductNameIndexer
{
    public Task IndexNewProductNames(
        IReadOnlyList<IProductInfo> products,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>>> FindSimilarProductNamesAsync(
        IReadOnlyList<(string ListingId, string Title)> listings,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>>>(
            new Dictionary<string, IReadOnlyList<SimilarProductName>>());
}
