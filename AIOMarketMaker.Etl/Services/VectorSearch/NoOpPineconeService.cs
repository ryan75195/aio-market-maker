namespace AIOMarketMaker.Etl.Services.VectorSearch;

/// <summary>
/// No-op Pinecone service used when Pinecone is not configured.
/// </summary>
public class NoOpPineconeService : IPineconeService
{
    public Task UpsertProductNamesAsync(
        IReadOnlyList<ProductNameVector> productNames,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<SimilarProductName>> SearchSimilarAsync(
        float[] queryEmbedding,
        int topK = 5,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SimilarProductName>>([]);

    public Task<HashSet<string>> GetExistingProductNamesAsync(CancellationToken ct = default)
        => Task.FromResult(new HashSet<string>());
}
