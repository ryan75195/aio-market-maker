using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Etl.Services.EntityResolution;

/// <summary>
/// Service for resolving entity classification and normalizing product attributes using an LLM.
/// </summary>
public interface IEntityResolutionService
{
    /// <summary>
    /// Resolves entity classification and normalizes attributes for a batch of products.
    /// Categories are absolute (describing what the item IS), not relative to search terms.
    /// </summary>
    /// <param name="products">Products from scraper</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Resolution results with category and normalized fields</returns>
    /// <exception cref="EntityResolutionException">Thrown on API failure or invalid response</exception>
    Task<IReadOnlyList<EntityResolutionResult>> ResolveAsync(
        IReadOnlyList<EbayProduct> products,
        CancellationToken ct = default);
}

/// <summary>
/// Exception thrown when entity resolution fails.
/// </summary>
public class EntityResolutionException : Exception
{
    public EntityResolutionException(string message) : base(message) { }
    public EntityResolutionException(string message, Exception innerException) : base(message, innerException) { }
}
