using System.Web;

namespace AIOMarketMaker.Services
{
    public interface IEbayUrlBuilder
    {
        string BuildSearchUrl(string query, bool sold, int page = 1);

        string BuildListingUrl(string itemId);
    }

    public class EbayUrlBuilder : IEbayUrlBuilder
    {
        private const string BaseSearchUrl = "https://www.ebay.co.uk/sch/i.html";
        private const string BaseItemUrl = "https://www.ebay.co.uk/itm/";

        public string BuildSearchUrl(string query, bool sold, int page = 1)
        {
            var flags = $"{(sold ? "&LH_Sold=1" : string.Empty)}" +
                        $"{(sold ? "&LH_Complete=1" : string.Empty)}";

            return $"{BaseSearchUrl}?_nkw={HttpUtility.UrlEncode(query)}{flags}" +
                   $"&_pgn={page}&_ipg=240&LH_TitleDesc=1";
        }

        public string BuildListingUrl(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be null or empty.", nameof(itemId));

            return $"{BaseItemUrl}{HttpUtility.UrlEncode(itemId)}";
        }
    }
}
