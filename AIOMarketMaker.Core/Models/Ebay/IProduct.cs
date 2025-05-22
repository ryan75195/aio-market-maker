// Services/EbayScraper.cs
namespace AIOMarketMaker.Models.Ebay
{
    public interface IProduct
    {
        string? ListingId { get; init; }

        string? Title { get; init; }

        decimal? Price { get; init; }
    
        string? Currency {  get; init; }

        string? Url { get; init; }

    }
}