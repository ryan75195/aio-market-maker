namespace AIOMarketMaker.Etl.Services.VectorSearch;

/// <summary>
/// No-op embedding service used when Pinecone is not configured.
/// </summary>
public class NoOpEmbeddingService : IEmbeddingService
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<float[]>>([]);
}
