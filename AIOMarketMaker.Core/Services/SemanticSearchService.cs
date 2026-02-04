using Microsoft.Extensions.Logging;
using Pinecone;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface ISemanticSearchService
{
    Task<IndexResult> IndexListingsAsync(
        IEnumerable<Listing> listings,
        CancellationToken ct = default);

    Task<SemanticSearchResult> SearchAsync(
        string queryText,
        IEnumerable<string>? filterToListingIds = null,
        int? topK = null,
        CancellationToken ct = default);

    Task<SemanticSearchResult> FindSimilarAsync(
        string listingId,
        IEnumerable<string>? filterToListingIds = null,
        int? topK = null,
        CancellationToken ct = default);

    Task DeleteAsync(
        IEnumerable<string> listingIds,
        CancellationToken ct = default);

    Task<bool> ExistsAsync(
        string listingId,
        CancellationToken ct = default);
}

public class SemanticSearchService : ISemanticSearchService
{
    private readonly IPineconeIndexClient _index;
    private readonly IEmbeddingService _embeddingService;
    private readonly PineconeConfig _config;
    private readonly ILogger<SemanticSearchService> _logger;

    public SemanticSearchService(
        PineconeConfig config,
        IPineconeIndexClient index,
        IEmbeddingService embeddingService,
        ILogger<SemanticSearchService> logger)
    {
        _config = config;
        _index = index;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<IndexResult> IndexListingsAsync(
        IEnumerable<Listing> listings,
        CancellationToken ct = default)
    {
        var listingsList = listings.ToList();
        if (listingsList.Count == 0)
        {
            return new IndexResult(0, 0, Array.Empty<string>());
        }

        _logger.LogInformation("Indexing {Count} listings to Pinecone", listingsList.Count);

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

                if (validItems.Count == 0) continue;

                var embeddings = await _embeddingService.GetEmbeddings(
                    validItems.Select(x => x.text),
                    ct);

                var vectors = validItems
                    .Zip(embeddings, (item, embedding) => new Vector
                    {
                        Id = item.listing.ListingId,
                        Values = embedding,
                        Metadata = new Metadata
                        {
                            ["listingId"] = item.listing.ListingId
                        }
                    })
                    .ToList();

                await _index.Upsert(new UpsertRequest { Vectors = vectors });
                upsertedCount += vectors.Count;

                _logger.LogDebug("Upserted batch of {Count} vectors", vectors.Count);
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

    public async Task<SemanticSearchResult> SearchAsync(
        string queryText,
        IEnumerable<string>? filterToListingIds = null,
        int? topK = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            throw new ArgumentException("Query text cannot be empty", nameof(queryText));
        }

        var filterIds = filterToListingIds?.ToList();
        _logger.LogDebug("Searching for: {Query} (filter to {Count} IDs)",
            queryText, filterIds?.Count ?? -1);

        var queryEmbedding = await _embeddingService.GetEmbedding(queryText, ct);

        ct.ThrowIfCancellationRequested();

        var request = new QueryRequest
        {
            Vector = queryEmbedding,
            TopK = (uint)(topK ?? _config.TopK),
            IncludeMetadata = false,
            IncludeValues = false,
            Filter = BuildIdFilter(filterIds)
        };

        var response = await _index.Query(request);

        var hits = response.Matches?
            .Where(m => m.Score >= _config.SimilarityThreshold)
            .Select(m => new SemanticSearchHit(m.Id, m.Score ?? 0f))
            .ToList() ?? [];

        return new SemanticSearchResult(hits);
    }

    public async Task<SemanticSearchResult> FindSimilarAsync(
        string listingId,
        IEnumerable<string>? filterToListingIds = null,
        int? topK = null,
        CancellationToken ct = default)
    {
        var filterIds = filterToListingIds?.ToList();
        _logger.LogDebug("Finding similar to: {ListingId} (filter to {Count} IDs)",
            listingId, filterIds?.Count ?? -1);

        ct.ThrowIfCancellationRequested();

        var request = new QueryRequest
        {
            Id = listingId,
            TopK = (uint)((topK ?? _config.TopK) + 1),
            IncludeMetadata = false,
            IncludeValues = false,
            Filter = BuildIdFilter(filterIds)
        };

        var response = await _index.Query(request);

        var hits = response.Matches?
            .Where(m => m.Id != listingId)
            .Where(m => m.Score >= _config.SimilarityThreshold)
            .Take(topK ?? _config.TopK)
            .Select(m => new SemanticSearchHit(m.Id, m.Score ?? 0f))
            .ToList() ?? [];

        return new SemanticSearchResult(hits);
    }

    public async Task DeleteAsync(
        IEnumerable<string> listingIds,
        CancellationToken ct = default)
    {
        var ids = listingIds.ToList();
        if (ids.Count == 0) return;

        _logger.LogInformation("Deleting {Count} listings from index", ids.Count);

        ct.ThrowIfCancellationRequested();

        await _index.Delete(new DeleteRequest { Ids = ids });
    }

    public async Task<bool> ExistsAsync(
        string listingId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var response = await _index.Fetch(new FetchRequest { Ids = [listingId] });
        return response.Vectors?.ContainsKey(listingId) ?? false;
    }

    private static string BuildEmbeddingText(Listing listing)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(listing.Title))
            parts.Add(listing.Title);

        if (!string.IsNullOrWhiteSpace(listing.Description))
            parts.Add(listing.Description);

        return string.Join(" ", parts);
    }

    private static Metadata? BuildIdFilter(List<string>? listingIds)
    {
        if (listingIds == null || listingIds.Count == 0)
            return null;

        return new Metadata
        {
            ["listingId"] = new Metadata { ["$in"] = listingIds }
        };
    }
}
