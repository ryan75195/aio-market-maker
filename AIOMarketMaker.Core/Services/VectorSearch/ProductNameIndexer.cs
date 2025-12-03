using System.Collections.Concurrent;
using AIOMarketMaker.Core.Configuration;
using AIOMarketMaker.Core.Models;
using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services.VectorSearch;

public class ProductNameIndexer : IProductNameIndexer
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IPineconeService _pineconeService;
    private readonly PineconeSettings _settings;
    private readonly ILogger<ProductNameIndexer> _logger;
    private readonly HashSet<string> _indexedProductNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _searchSemaphore;

    public ProductNameIndexer(
        IEmbeddingService embeddingService,
        IPineconeService pineconeService,
        PineconeSettings settings,
        ILogger<ProductNameIndexer> logger)
    {
        _embeddingService = embeddingService;
        _pineconeService = pineconeService;
        _settings = settings;
        _logger = logger;
        _searchSemaphore = new SemaphoreSlim(settings.MaxSearchConcurrency);
    }

    public async Task IndexNewProductNames(
        IReadOnlyList<IProductInfo> products,
        CancellationToken ct = default)
    {
        var newProductNames = products
            .Where(p => !string.IsNullOrEmpty(p.ProductName))
            .Where(p => !_indexedProductNames.Contains(p.ProductName!))
            .GroupBy(p => p.ProductName!.ToLowerInvariant())
            .Select(g => g.First())
            .ToList();

        if (newProductNames.Count == 0)
        {
            _logger.LogDebug("No new product names to index");
            return;
        }

        _logger.LogInformation("Indexing {Count} new product names", newProductNames.Count);

        var productNames = newProductNames.Select(p => p.ProductName!).ToList();
        var embeddings = await _embeddingService.GenerateEmbeddingsAsync(productNames, ct);

        var vectors = newProductNames.Select((p, i) => new ProductNameVector(
            p.Id,
            p.ProductName!,
            p.Category,
            p.Brand,
            embeddings[i]
        )).ToList();

        await _pineconeService.UpsertProductNamesAsync(vectors, ct);

        foreach (var name in productNames)
        {
            _indexedProductNames.Add(name);
        }

        _logger.LogInformation("Successfully indexed {Count} product names", newProductNames.Count);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>>> FindSimilarProductNamesAsync(
        IReadOnlyList<(string ListingId, string Title)> listings,
        CancellationToken ct = default)
    {
        var result = new ConcurrentDictionary<string, IReadOnlyList<SimilarProductName>>();

        if (listings.Count == 0)
            return result;

        var titles = listings.Select(l => l.Title).ToList();
        var embeddings = await _embeddingService.GenerateEmbeddingsAsync(titles, ct);

        _logger.LogInformation("Searching Pinecone for {Count} similar product names ({Concurrency} parallel)",
            listings.Count, _settings.MaxSearchConcurrency);

        // Search in parallel with concurrency limit
        var tasks = listings.Select((listing, index) => SearchSimilarAsync(
            listing.ListingId, embeddings[index], result, ct)).ToList();

        await Task.WhenAll(tasks);

        _logger.LogDebug("Found similar product names for {Count}/{Total} listings",
            result.Count, listings.Count);

        return result;
    }

    private async Task SearchSimilarAsync(
        string listingId,
        float[] embedding,
        ConcurrentDictionary<string, IReadOnlyList<SimilarProductName>> results,
        CancellationToken ct)
    {
        await _searchSemaphore.WaitAsync(ct);
        try
        {
            var similar = await _pineconeService.SearchSimilarAsync(
                embedding,
                _settings.TopK,
                ct);

            if (similar.Count > 0)
            {
                results[listingId] = similar;
            }
        }
        finally
        {
            _searchSemaphore.Release();
        }
    }
}
