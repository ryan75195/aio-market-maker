using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IListingIndexingService
{
    Task<IndexingResult> Index(Listing listing, bool embedContent, CancellationToken ct = default);
}

public record IndexingResult(IndexingAction Action, string? Error = null);

public enum IndexingAction
{
    Embedded,
    Skipped
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

public class NullListingIndexingService : IListingIndexingService
{
    public Task<IndexingResult> Index(Listing listing, bool embedContent, CancellationToken ct = default)
        => Task.FromResult(new IndexingResult(IndexingAction.Skipped));
}
