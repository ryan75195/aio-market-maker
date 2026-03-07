using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace AIOMarketMaker.Core.Services;

public enum EmbeddingModel
{
    Large,
    Small
}

public record EmbeddingConfig(string ApiKey, string Model = "text-embedding-3-small", int Dimensions = 1536);

public interface IEmbeddingService
{
    Task<float[]> GetEmbedding(string text, CancellationToken ct = default, EmbeddingModel model = EmbeddingModel.Large);
    Task<float[][]> GetEmbeddings(IEnumerable<string> texts, CancellationToken ct = default, EmbeddingModel model = EmbeddingModel.Large);
}

public class EmbeddingService : IEmbeddingService
{
    private static readonly Dictionary<EmbeddingModel, (string Model, int Dimensions)> ModelSpecs = new()
    {
        [EmbeddingModel.Large] = ("text-embedding-3-large", 3072),
        [EmbeddingModel.Small] = ("text-embedding-3-small", 1536),
    };

    private readonly Dictionary<EmbeddingModel, EmbeddingClient> _clients = new();
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(EmbeddingConfig config, ILogger<EmbeddingService> logger)
    {
        _logger = logger;
        foreach (var (model, spec) in ModelSpecs)
        {
            _clients[model] = new EmbeddingClient(spec.Model, config.ApiKey);
        }
    }

    public async Task<float[]> GetEmbedding(string text, CancellationToken ct = default, EmbeddingModel model = EmbeddingModel.Large)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text cannot be null or empty", nameof(text));
        }

        var (client, dims) = GetClientAndDims(model);
        var options = new EmbeddingGenerationOptions { Dimensions = dims };

        var response = await client.GenerateEmbeddingAsync(text, options, ct);
        return response.Value.ToFloats().ToArray();
    }

    public async Task<float[][]> GetEmbeddings(IEnumerable<string> texts, CancellationToken ct = default, EmbeddingModel model = EmbeddingModel.Large)
    {
        var textList = texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        if (textList.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var (client, dims) = GetClientAndDims(model);
        var options = new EmbeddingGenerationOptions { Dimensions = dims };

        var response = await client.GenerateEmbeddingsAsync(textList, options, ct);

        return response.Value
            .Select(e => e.ToFloats().ToArray())
            .ToArray();
    }

    private (EmbeddingClient Client, int Dimensions) GetClientAndDims(EmbeddingModel model)
    {
        return (_clients[model], ModelSpecs[model].Dimensions);
    }
}
