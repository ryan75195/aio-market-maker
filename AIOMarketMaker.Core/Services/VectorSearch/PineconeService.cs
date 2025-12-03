using System.Security.Cryptography;
using System.Text;
using AIOMarketMaker.Core.Configuration;
using Microsoft.Extensions.Logging;
using Pinecone;
using Pinecone.Rest;

namespace AIOMarketMaker.Core.Services.VectorSearch;

public class PineconeService : IPineconeService
{
    private readonly PineconeSettings _settings;
    private readonly ILogger<PineconeService> _logger;
    private readonly PineconeClient _client;
    private Index<RestTransport>? _index;

    public PineconeService(
        PineconeSettings settings,
        ILogger<PineconeService> logger)
    {
        _settings = settings;
        _logger = logger;
        _client = new PineconeClient(_settings.ApiKey);
    }

    private async Task<Index<RestTransport>> GetIndexAsync()
    {
        if (_index == null)
        {
            _logger.LogInformation("Connecting to Pinecone index: {IndexName}", _settings.IndexName);
            try
            {
                // Use GetIndex with the index name - REST transport is recommended over gRPC
                _index = await _client.GetIndex<RestTransport>(_settings.IndexName);
                _logger.LogInformation("Successfully connected to Pinecone index: {IndexName}", _settings.IndexName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Pinecone index '{IndexName}'. API Key starts with: {ApiKeyPrefix}",
                    _settings.IndexName, _settings.ApiKey?[..Math.Min(8, _settings.ApiKey?.Length ?? 0)] + "...");
                throw;
            }
        }
        return _index;
    }

    public async Task UpsertProductNamesAsync(
        IReadOnlyList<ProductNameVector> productNames,
        CancellationToken ct = default)
    {
        if (productNames.Count == 0)
            return;

        var index = await GetIndexAsync();

        var vectors = productNames.Select(p => new Vector
        {
            Id = GenerateVectorId(p.ProductName),
            Values = p.Embedding,
            Metadata = new MetadataMap
            {
                ["productName"] = p.ProductName,
                ["productId"] = p.ProductId.ToString(),
                ["category"] = p.Category ?? "",
                ["brand"] = p.Brand ?? ""
            }
        }).ToArray();

        _logger.LogInformation("Upserting {Count} product name vectors to Pinecone", vectors.Length);

        var batchNum = 0;
        foreach (var batch in vectors.Chunk(100))
        {
            batchNum++;
            try
            {
                _logger.LogDebug("Upserting batch {BatchNum} with {Count} vectors", batchNum, batch.Length);
                await index.Upsert(batch);
                _logger.LogDebug("Successfully upserted batch {BatchNum}", batchNum);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert batch {BatchNum} to Pinecone. First vector ID: {FirstId}",
                    batchNum, batch.FirstOrDefault()?.Id);
                throw;
            }
        }

        _logger.LogInformation("Successfully upserted all {Count} vectors to Pinecone", vectors.Length);
    }

    public async Task<IReadOnlyList<SimilarProductName>> SearchSimilarAsync(
        float[] queryEmbedding,
        int topK = 5,
        CancellationToken ct = default)
    {
        var index = await GetIndexAsync();

        _logger.LogDebug("Querying Pinecone for top {TopK} similar vectors (embedding length: {Length})",
            topK, queryEmbedding.Length);

        ScoredVector[] response;
        try
        {
            response = await index.Query(queryEmbedding, (uint)topK, includeMetadata: true);
            _logger.LogDebug("Pinecone query returned {Count} results", response.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pinecone query failed. Embedding length: {Length}, TopK: {TopK}",
                queryEmbedding.Length, topK);
            throw;
        }

        var results = new List<SimilarProductName>();

        foreach (var match in response)
        {
            if (match.Score < _settings.SimilarityThreshold)
                continue;

            var productName = "";
            string? category = null;
            string? brand = null;

            if (match.Metadata != null)
            {
                if (match.Metadata.TryGetValue("productName", out var nameValue))
                    productName = nameValue.ToString();
                if (match.Metadata.TryGetValue("category", out var catValue))
                    category = catValue.ToString();
                if (match.Metadata.TryGetValue("brand", out var brandValue))
                    brand = brandValue.ToString();
            }

            if (!string.IsNullOrEmpty(productName))
            {
                results.Add(new SimilarProductName(
                    productName,
                    string.IsNullOrEmpty(category) ? null : category,
                    string.IsNullOrEmpty(brand) ? null : brand,
                    (float)match.Score
                ));
            }
        }

        return results;
    }

    public Task<HashSet<string>> GetExistingProductNamesAsync(CancellationToken ct = default)
    {
        // Pinecone doesn't have a direct "list all" capability for serverless indexes
        // We'll track this in local cache during the session
        return Task.FromResult(new HashSet<string>());
    }

    private static string GenerateVectorId(string productName)
    {
        var normalized = productName.ToLowerInvariant().Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }
}
