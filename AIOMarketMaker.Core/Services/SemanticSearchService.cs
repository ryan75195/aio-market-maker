using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface ISemanticSearchService
{
    Task<IndexResult> IndexListings(
        IEnumerable<Listing> listings,
        CancellationToken ct = default);

    Task<SemanticSearchResult> Search(
        string queryText,
        int? topK = null,
        CancellationToken ct = default);

    Task<SemanticSearchResult> FindSimilar(
        string listingId,
        int? topK = null,
        CancellationToken ct = default);

    Task Delete(
        IEnumerable<string> listingIds,
        CancellationToken ct = default);

    Task<bool> Exists(
        string listingId,
        CancellationToken ct = default);
}

public class SemanticSearchService : ISemanticSearchService
{
    private readonly IVectorIndex _vectorIndex;
    private readonly IEmbeddingService _embeddingService;
    private readonly VectorIndexConfig _config;
    private readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(
        VectorIndexConfig config,
        IVectorIndex vectorIndex,
        IEmbeddingService embeddingService,
        ILogger<SemanticSearchService> logger)
    {
        _config = config;
        _vectorIndex = vectorIndex;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<IndexResult> IndexListings(
        IEnumerable<Listing> listings,
        CancellationToken ct = default)
    {
        var listingsList = listings.ToList();
        if (listingsList.Count == 0)
        {
            return new IndexResult(0, 0, Array.Empty<string>());
        }

        _logger.LogInformation("Indexing {Count} listings to vector index", listingsList.Count);

        var errors = new List<string>();
        var upsertedCount = 0;
        var skippedCount = 0;

        var batches = listingsList.Chunk(_config.UpsertBatchSize);

        foreach (var batch in batches)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var textsToEmbed = batch
                    .Select(BuildEmbeddingText)
                    .ToList();

                var validItems = batch
                    .Zip(textsToEmbed, (listing, text) => (listing, text))
                    .Where(x => !string.IsNullOrWhiteSpace(x.text))
                    .ToList();

                skippedCount += batch.Length - validItems.Count;

                if (validItems.Count == 0)
                {
                    continue;
                }

                var embeddings = await _embeddingService.GetEmbeddings(
                    validItems.Select(x => x.text),
                    ct);

                var items = validItems
                    .Zip(embeddings, (item, embedding) => (Id: item.listing.ListingId, Vector: embedding));

                _vectorIndex.UpsertBatch(items);
                upsertedCount += validItems.Count;

                _logger.LogDebug("Upserted batch of {Count} vectors", validItems.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upsert batch");
                errors.Add($"Batch failed: {ex.Message}");
            }
        }

        _logger.LogInformation("Indexing complete: {Upserted} upserted, {Skipped} skipped, {Errors} errors",
            upsertedCount, skippedCount, errors.Count);

        return new IndexResult(upsertedCount, skippedCount, errors);
    }

    public async Task<SemanticSearchResult> Search(
        string queryText,
        int? topK = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw new ArgumentException("Query text cannot be empty", nameof(queryText));
        }

        _logger.LogDebug("Searching for: {Query}", queryText);

        var queryEmbedding = await _embeddingService.GetEmbedding(queryText, ct);

        ct.ThrowIfCancellationRequested();

        var hits = _vectorIndex.Search(queryEmbedding, topK ?? _config.TopK)
            .Where(h => h.Score >= _config.SimilarityThreshold)
            .Select(h => new SemanticSearchHit(h.Id, h.Score))
            .ToList();

        return new SemanticSearchResult(hits);
    }

    public Task<SemanticSearchResult> FindSimilar(
        string listingId,
        int? topK = null,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Finding similar to: {ListingId}", listingId);

        ct.ThrowIfCancellationRequested();

        var effectiveTopK = topK ?? _config.TopK;

        var hits = _vectorIndex.SearchById(listingId, effectiveTopK + 1)
            .Where(h => h.Id != listingId)
            .Where(h => h.Score >= _config.SimilarityThreshold)
            .Take(effectiveTopK)
            .Select(h => new SemanticSearchHit(h.Id, h.Score))
            .ToList();

        return Task.FromResult(new SemanticSearchResult(hits));
    }

    public Task Delete(
        IEnumerable<string> listingIds,
        CancellationToken ct = default)
    {
        var ids = listingIds.ToList();
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Deleting {Count} listings from index", ids.Count);

        ct.ThrowIfCancellationRequested();

        _vectorIndex.Remove(ids);
        return Task.CompletedTask;
    }

    public Task<bool> Exists(
        string listingId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_vectorIndex.Contains(listingId));
    }

    private static string BuildEmbeddingText(Listing listing)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(listing.Title))
        {
            parts.Add(listing.Title);
        }

        if (!string.IsNullOrWhiteSpace(listing.Description))
        {
            parts.Add(listing.Description);
        }

        return string.Join(" ", parts);
    }
}
