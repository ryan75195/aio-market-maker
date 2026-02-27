using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IListingIndexingService
{
    Task<IndexingResult> Index(Listing listing, bool embedContent, CancellationToken ct = default);
    Task<IEnumerable<IndexingResult>> IndexBatch(IEnumerable<Listing> listings, bool embedContent, CancellationToken ct = default);
}

public record IndexingResult(IndexingAction Action, string? Error = null);

public enum IndexingAction
{
    Embedded,
    Skipped,
    Failed
}

public class ListingIndexingService : IListingIndexingService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorIndex _vectorIndex;
    private readonly ILogger<ListingIndexingService> _logger;

    public ListingIndexingService(
        IEmbeddingService embeddingService,
        IVectorIndex vectorIndex,
        ILogger<ListingIndexingService> logger)
    {
        _embeddingService = embeddingService;
        _vectorIndex = vectorIndex;
        _logger = logger;
    }

    public async Task<IndexingResult> Index(Listing listing, bool embedContent, CancellationToken ct = default)
    {
        var text = BuildEmbeddingText(listing);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Skipping indexing for {ListingId}: no title or description", listing.ListingId);
            return new IndexingResult(IndexingAction.Skipped);
        }

        if (!embedContent)
        {
            return new IndexingResult(IndexingAction.Skipped);
        }

        var embedding = await _embeddingService.GetEmbedding(text, ct);
        _vectorIndex.Upsert(listing.ListingId, embedding);

        _logger.LogInformation("Embedded and indexed listing {ListingId}", listing.ListingId);
        return new IndexingResult(IndexingAction.Embedded);
    }

    public async Task<IEnumerable<IndexingResult>> IndexBatch(
        IEnumerable<Listing> listings, bool embedContent, CancellationToken ct = default)
    {
        var listingList = listings.ToList();

        if (!embedContent || listingList.Count == 0)
        {
            return listingList.Select(_ => new IndexingResult(IndexingAction.Skipped));
        }

        // Build texts and track which listings have embeddable content
        var indexable = new List<(int Index, Listing Listing, string Text)>();
        var results = new IndexingResult[listingList.Count];

        for (var i = 0; i < listingList.Count; i++)
        {
            var text = BuildEmbeddingText(listingList[i]);
            if (string.IsNullOrWhiteSpace(text))
            {
                results[i] = new IndexingResult(IndexingAction.Skipped);
            }
            else
            {
                indexable.Add((i, listingList[i], text));
            }
        }

        if (indexable.Count == 0)
        {
            return results;
        }

        // Single batch API call for all texts
        float[][] embeddings;
        try
        {
            embeddings = await _embeddingService.GetEmbeddings(indexable.Select(x => x.Text), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Batch embedding failed for {Count} listings, falling back to individual calls", indexable.Count);
            return await FallbackToIndividual(listingList, ct);
        }

        for (var i = 0; i < indexable.Count; i++)
        {
            var (originalIndex, listing, _) = indexable[i];
            if (i < embeddings.Length && embeddings[i] != null)
            {
                _vectorIndex.Upsert(listing.ListingId, embeddings[i]);
                results[originalIndex] = new IndexingResult(IndexingAction.Embedded);
            }
            else
            {
                results[originalIndex] = new IndexingResult(IndexingAction.Failed, "Embedding returned null");
            }
        }

        _logger.LogInformation("Batch indexed {Embedded}/{Total} listings",
            indexable.Count, listingList.Count);

        return results;
    }

    private async Task<IEnumerable<IndexingResult>> FallbackToIndividual(
        List<Listing> listings, CancellationToken ct)
    {
        var results = new IndexingResult[listings.Count];
        for (var i = 0; i < listings.Count; i++)
        {
            try
            {
                results[i] = await Index(listings[i], embedContent: true, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Individual embedding failed for {ListingId}", listings[i].ListingId);
                results[i] = new IndexingResult(IndexingAction.Failed, ex.Message);
            }
        }
        return results;
    }

    internal static string BuildEmbeddingText(Listing listing)
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

public class NullListingIndexingService : IListingIndexingService
{
    public Task<IndexingResult> Index(Listing listing, bool embedContent, CancellationToken ct = default)
        => Task.FromResult(new IndexingResult(IndexingAction.Skipped));

    public Task<IEnumerable<IndexingResult>> IndexBatch(IEnumerable<Listing> listings, bool embedContent, CancellationToken ct = default)
        => Task.FromResult(listings.Select(_ => new IndexingResult(IndexingAction.Skipped)));
}
