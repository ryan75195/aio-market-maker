using System.Text.Json.Serialization;

namespace AIOMarketMaker.Models.Ebay
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EbayListingStatus
    {
        Active,
        Sold,
        Ended,
        OutOfStock,
        Unknown
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum PurchaseFormat
    {
        Auction,
        AuctionWithBestOffer,
        BuyItNow,
        BuyItNowWithBestOffer,
        Unknown
    }

    public record EbayProduct(
        string? ListingId,
        string? Title,
        decimal? Price,
        string? Currency,
        decimal? ShippingCost,
        string? Url,
        Condition? Condition,
        IEnumerable<string>? Images,
        EbayListingStatus? ListingStatus,
        PurchaseFormat? PurchaseFormat,
        string? Description,
        DateTime? EndDateUtc,
        bool IsSold
    ) : IEbayProductSummary;
}
