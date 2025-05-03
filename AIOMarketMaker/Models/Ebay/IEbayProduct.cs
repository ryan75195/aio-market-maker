namespace AIOMarketMaker.Models.Ebay
{
    public interface IEbayProduct : IEbayProductSummary
    {
        string? ItemSpecifics { get; init; }
        string? Description { get; init; }
        string? Condition { get; init; }
    }

    public record EbayProduct(
        string Id,
        string Name,
        decimal? Price,
        string? Currency,
        decimal? ShippingCost,
        string? Condition,
        IEnumerable<string> Images,
        string? ItemSpecifics,
        string? Description,
        string? Url,
        DateTime? SoldDateUtc // Null means it's active
    ) : IEbayProduct;
}
