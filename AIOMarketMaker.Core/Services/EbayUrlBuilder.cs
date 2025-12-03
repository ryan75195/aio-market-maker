using System.Web;

namespace AIOMarketMaker.Core.Services
{
    public interface IEbayUrlBuilder
    {
        string BuildSearchUrl(string query, bool sold, int page, Condition condition, BuyingFormat buyingFormat);

        string BuildListingUrl(string itemId);
    }

    public class EbayUrlBuilder : IEbayUrlBuilder
    {
        private const string BaseSearchUrl = "https://www.ebay.co.uk/sch/i.html";
        private const string BaseItemUrl = "https://www.ebay.co.uk/itm/";

        public string BuildSearchUrl(string query, bool sold, int page, Condition condition, BuyingFormat buyingFormat)
        {
            var flags = $"{(sold ? "&LH_Sold=1" : string.Empty)}" +
                        $"{(sold ? "&LH_Complete=1" : string.Empty)}" +

                        $"{(buyingFormat == BuyingFormat.BUY_NOW ? $"&LH_BIN=1" : string.Empty)}" +
                        $"{(buyingFormat == BuyingFormat.AUCTION ? $"&LH_Auction=1" : string.Empty)}" +
                        $"{(buyingFormat == BuyingFormat.ALL ? $"&LH_All=1" : string.Empty)}" +

                        $"{(condition != null ? $"&LH_ItemCondition={this.GetConditionValue(condition)}" : string.Empty)}";


            return $"{BaseSearchUrl}?_nkw={HttpUtility.UrlEncode(query)}{flags}" +
                   $"&_pgn={page}&_ipg=240&LH_TitleDesc=1";
        }

        private int GetConditionValue(Condition condition)
        {
            switch (condition)
            {
                case Condition.NEW:
                    return 1000;
                case Condition.USED:
                    return 3000;
                case Condition.FOR_PARTS_NOT_WORKING:
                    return 7000;
                case Condition.GOOD_REFURBISHED:
                    return 2030;
                case Condition.VERY_GOOD_REFURBISHED:
                    return 2020;
                case Condition.EXCELLENT_REFURBISHED:
                    return 2010;
                case Condition.OPENED_NEVER_USED:
                    return 1500;
                default:
                    return 0;
            }
        }

        public string BuildListingUrl(string itemId)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                throw new ArgumentException("Item ID cannot be null or empty.", nameof(itemId));

            return $"{BaseItemUrl}{HttpUtility.UrlEncode(itemId)}";
        }
    }
}
