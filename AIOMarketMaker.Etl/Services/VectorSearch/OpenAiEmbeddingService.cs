using System.Collections.Concurrent;
using AIOMarketMaker.Etl.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Embeddings;

namespace AIOMarketMaker.Etl.Services.VectorSearch;

public class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly OpenAIClient _client;
    private readonly EmbeddingSettings _settings;
    private readonly ILogger<OpenAiEmbeddingService> _logger;
    private readonly SemaphoreSlim _semaphore;

    public OpenAiEmbeddingService(
        OpenAIClient client,
        EmbeddingSettings settings,
        ILogger<OpenAiEmbeddingService> logger)
    {
        _client = client;
        _settings = settings;
        _logger = logger;
        _semaphore = new SemaphoreSlim(settings.MaxConcurrency);
    }

    public async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var results = await GenerateEmbeddingsAsync([text], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        if (texts.Count == 0)
            return [];

        var embeddingClient = _client.GetEmbeddingClient(_settings.Model);
        var batches = texts.Chunk(_settings.MaxBatchSize).ToList();

        _logger.LogInformation("Generating embeddings for {Count} texts in {Batches} batches (max {Concurrency} parallel)",
            texts.Count, batches.Count, _settings.MaxConcurrency);

        // Process batches in parallel with concurrency limit
        var batchResults = new ConcurrentDictionary<int, float[][]>();

        var tasks = batches.Select((batch, index) => ProcessBatchAsync(
            embeddingClient, batch, index, batchResults, ct)).ToList();

        await Task.WhenAll(tasks);

        // Flatten results in order
        var results = new List<float[]>();
        for (int i = 0; i < batches.Count; i++)
        {
            if (batchResults.TryGetValue(i, out var embeddings))
            {
                results.AddRange(embeddings);
            }
        }

        return results;
    }

    private async Task ProcessBatchAsync(
        EmbeddingClient embeddingClient,
        string[] batch,
        int batchIndex,
        ConcurrentDictionary<int, float[][]> results,
        CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            _logger.LogDebug("Processing batch {Index} with {Count} texts", batchIndex, batch.Length);

            var response = await embeddingClient.GenerateEmbeddingsAsync(batch, cancellationToken: ct);

            var embeddings = response.Value
                .Select(e => e.ToFloats().ToArray())
                .ToArray();

            results[batchIndex] = embeddings;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
