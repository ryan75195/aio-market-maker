// Services/EbayScraper.cs
namespace AIOMarketMaker.Models.Ebay
{
    public interface IProduct
    {
        string id { get; init; }

        string title { get; init; }

        decimal? price { get; init; }
    
        string? currency {  get; init; }

        string? url { get; init; }

    }
}