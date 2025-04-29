// Services/EbayScraper.cs
namespace AIOMarketMaker.Models.Ebay
{
    public interface IEbayProductSummary : IProduct
    {
        decimal? ShippingCost { get; init; }
        IEnumerable<string> Images { get; init; }
    }

    public record SoldEbayProductSummary(
           string Id,
           string Name,
           decimal? Price,
           string? Currency,
           decimal? ShippingCost,
           IEnumerable<string> Images,
           string? Url,
           DateTime? SoldDateUtc
       ) : IEbayProductSummary;

    public record ActiveEbayProductSummary(
        string Id,
        string Name,
        decimal? Price,
        string? Currency,
        decimal? ShippingCost,
        IEnumerable<string> Images,
        string? Url
    ) : IEbayProductSummary;

}