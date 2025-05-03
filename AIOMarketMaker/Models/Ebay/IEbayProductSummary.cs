// Services/EbayScraper.cs
namespace AIOMarketMaker.Models.Ebay
{
    public interface IEbayProductSummary : IProduct
    {
        decimal? shippingCost { get; init; }
        IEnumerable<string> images { get; init; }
    }

    public record EbayProductSummary(
           string id,
           string title,
           decimal? price,
           string? currency,
           decimal? shippingCost,
           IEnumerable<string> images,
           string? url,
           DateTime? soldDateUtc,
           BuyingFormat buyingFormat,
           Condition condition
    ) : IEbayProductSummary;
}