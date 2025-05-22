// Services/EbayScraper.cs
namespace AIOMarketMaker.Models.Ebay
{
    public interface IEbayProductSummary : IProduct
    {
        decimal? ShippingCost { get; init; }
        IEnumerable<string>? Images { get; init; }
    }

    public record EbayProductSummary(
           string? ListingId,
           string? Title,
           decimal? Price,
           string? Currency,
           decimal? ShippingCost,
           string? Url,
           BuyingFormat? BuyingFormat,
           Condition? Condition,
           IEnumerable<string>? Images,
           DateTime? EndDateUtc
    ) : IEbayProductSummary;
}