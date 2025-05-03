namespace AIOMarketMaker.Models.Ebay
{
    public interface IEbayProduct : IEbayProductSummary
    {
        string? ItemSpecifics { get; init; }
        string? Description { get; init; }
        string? Condition { get; init; }
    }

    public record EbayProduct(
        string id,
        string title,
        decimal? price,
        string? currency,
        decimal? shippingCost,
        string? Condition,
        IEnumerable<string> images,
        string? ItemSpecifics,
        string? Description,
        string? url,
        DateTime? SoldDateUtc // Null means it's active
    ) : IEbayProduct;
}
