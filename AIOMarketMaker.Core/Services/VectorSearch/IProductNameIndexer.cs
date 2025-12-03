using AIOMarketMaker.Core.Models;

namespace AIOMarketMaker.Core.Services.VectorSearch;

/// <summary>
/// High-level service that coordinates embedding generation and Pinecone indexing.
/// </summary>
public interface IProductNameIndexer
{
    /// <summary>
    /// Indexes new product names that don't already exist in Pinecone.
    /// </summary>
    Task IndexNewProductNames(
        IReadOnlyList<IProductInfo> products,
        CancellationToken ct = default);

    /// <summary>
    /// Finds similar existing product names for a list of eBay listing titles.
    /// Returns a dictionary mapping listing ID to similar product names found.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>>> FindSimilarProductNamesAsync(
        IReadOnlyList<(string ListingId, string Title)> listings,
        CancellationToken ct = default);
}
