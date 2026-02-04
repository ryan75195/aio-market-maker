using Microsoft.Extensions.Logging;
using Pinecone;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Core.Services;

public interface IListingIndexingService
{
    Task<IndexingResult> Index(Listing listing, bool isNew, CancellationToken ct = default);
}

public record IndexingResult(IndexingAction Action, string? Error = null);

public enum IndexingAction
{
    Embedded,
    MetadataUpdated,
    Skipped
}

public class ListingIndexingService : IListingIndexingService
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IPineconeIndexClient _pinecone;
    private readonly ILogger<ListingIndexingService> _logger;

    public ListingIndexingService(
        IEmbeddingService embeddingService,
        IPineconeIndexClient pinecone,
        ILogger<ListingIndexingService> logger)
    {
        _embeddingService = embeddingService;
        _pinecone = pinecone;
        _logger = logger;
    }

    public async Task<IndexingResult> Index(Listing listing, bool isNew, CancellationToken ct = default)
    {
        var text = BuildEmbeddingText(listing);

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Skipping indexing for {ListingId}: no title or description", listing.ListingId);
            return new IndexingResult(IndexingAction.Skipped);
        }

        var metadata = BuildMetadata(listing);

        if (isNew)
        {
            var embedding = await _embeddingService.GetEmbedding(text, ct);

            await _pinecone.Upsert(new UpsertRequest
            {
                Vectors = new[]
                {
                    new Vector
                    {
                        Id = listing.ListingId,
                        Values = embedding,
                        Metadata = metadata
                    }
                }
            }, ct);

            _logger.LogInformation("Embedded and indexed new listing {ListingId}", listing.ListingId);
            return new IndexingResult(IndexingAction.Embedded);
        }

        await _pinecone.Update(new UpdateRequest
        {
            Id = listing.ListingId,
            SetMetadata = metadata
        }, ct);

        _logger.LogInformation("Updated metadata for listing {ListingId}", listing.ListingId);
        return new IndexingResult(IndexingAction.MetadataUpdated);
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

    private static Metadata BuildMetadata(Listing listing)
    {
        var metadata = new Metadata
        {
            ["listingId"] = listing.ListingId,
            ["scrapeJobId"] = (double)listing.ScrapeJobId,
            ["condition"] = listing.Condition ?? "",
            ["listingStatus"] = listing.ListingStatus ?? "",
            ["purchaseFormat"] = listing.PurchaseFormat ?? "",
            ["createdUtc"] = listing.CreatedUtc.ToString("O")
        };

        if (listing.Price.HasValue)
        {
            metadata["price"] = (double)listing.Price.Value;
        }

        if (listing.ShippingCost.HasValue)
        {
            metadata["shippingCost"] = (double)listing.ShippingCost.Value;
        }

        return metadata;
    }
}
