namespace AIOMarketMaker.Etl.Services.VectorSearch;

/// <summary>
/// Service for generating text embeddings using OpenAI.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Generates an embedding vector for a single text input.
    /// </summary>
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Generates embedding vectors for multiple texts in a single batch call.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default);
}
