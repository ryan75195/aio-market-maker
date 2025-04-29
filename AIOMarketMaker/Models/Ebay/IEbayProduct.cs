namespace AIOMarketMaker.Models.Ebay
{
    public interface IEbayProduct : IEbayProductSummary
    {
        string? ItemSpecifics { get; init; }
        string? Description { get; init; }
        string? Condition { get; init; }
    }

    public record SoldEbayProduct(
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
        DateTime? SoldDateUtc
    ) : IEbayProduct;

    public record ActiveEbayProduct(
        string Id,
        string Name,
        decimal? Price,
        string? Currency,
        decimal? ShippingCost,
        string? Condition,
        IEnumerable<string> Images,
        string? ItemSpecifics,
        string? Description,
        string? Url
    ) : IEbayProduct;
}
