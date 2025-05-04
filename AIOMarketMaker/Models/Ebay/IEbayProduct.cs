namespace AIOMarketMaker.Models.Ebay
{
    public enum EbayListingStatus
    {
        Active,
        Sold,
        Ended,
        Unknown
    }

    public enum PurchaseFormat
    {
        Auction,
        AuctionWithBestOffer,
        BuyItNow,
        BuyItNowWithBestOffer,
        SoldByBid,
        SoldByBuyNow,
        AuctionEndedNoSale
    }

    public record EbayProduct(
        string ListingId,
        string Title,
        decimal Price,
        string Currency,
        decimal ShippingCost,
        string Url,
        Condition Condition,
        IEnumerable<string> Images,
        EbayListingStatus ListingStatus,
        PurchaseFormat PurchaseFormat,
        string? Description,
        string? ItemSpecifics, // make a dict
        DateTime? SoldDateUtc 
    ) : IEbayProductSummary;
}
