namespace AIOMarketMaker.Core.Services.VectorSearch;

/// <summary>
/// Service for indexing and searching product name vectors in Pinecone.
/// </summary>
public interface IPineconeService
{
    /// <summary>
    /// Upserts multiple product name embeddings in a batch.
    /// </summary>
    Task UpsertProductNamesAsync(
        IReadOnlyList<ProductNameVector> productNames,
        CancellationToken ct = default);

    /// <summary>
    /// Searches for similar product names given an embedding.
    /// </summary>
    Task<IReadOnlyList<SimilarProductName>> SearchSimilarAsync(
        float[] queryEmbedding,
        int topK = 5,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all existing product names from the index.
    /// </summary>
    Task<HashSet<string>> GetExistingProductNamesAsync(CancellationToken ct = default);
}
