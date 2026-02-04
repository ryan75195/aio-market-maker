using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace AIOMarketMaker.Core.Services;

public record EmbeddingConfig(string ApiKey, string Model = "text-embedding-3-small", int Dimensions = 1536);

public interface IEmbeddingService
{
    Task<float[]> GetEmbedding(string text, CancellationToken ct = default);
    Task<float[][]> GetEmbeddings(IEnumerable<string> texts, CancellationToken ct = default);
}

public class EmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _client;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly int _dimensions;

    public EmbeddingService(EmbeddingConfig config, ILogger<EmbeddingService> logger)
    {
        _client = new EmbeddingClient(config.Model, config.ApiKey);
        _logger = logger;
        _dimensions = config.Dimensions;
    }

    public async Task<float[]> GetEmbedding(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be null or empty", nameof(text));
        }

        var options = new EmbeddingGenerationOptions
        {
            Dimensions = _dimensions
        };

        var response = await _client.GenerateEmbeddingAsync(text, options, ct);
        return response.Value.ToFloats().ToArray();
    }

    public async Task<float[][]> GetEmbeddings(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        if (textList.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var options = new EmbeddingGenerationOptions
        {
            Dimensions = _dimensions
        };

        var response = await _client.GenerateEmbeddingsAsync(textList, options, ct);

        return response.Value
            .Select(e => e.ToFloats().ToArray())
            .ToArray();
    }
}
