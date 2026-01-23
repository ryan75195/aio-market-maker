using System.Web;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.E2E;

public class TestableEbayUrlBuilder : IEbayUrlBuilder
{
    private readonly string _baseUrl;

    public TestableEbayUrlBuilder(string baseUrl)
    {
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public string BuildSearchUrl(string query, bool sold, int page, Condition condition, BuyingFormat buyingFormat)
    {
        var flags = $"{(sold ? "&LH_Sold=1&LH_Complete=1" : string.Empty)}" +
                    $"{(buyingFormat == BuyingFormat.BUY_NOW ? "&LH_BIN=1" : string.Empty)}" +
                    $"{(buyingFormat == BuyingFormat.AUCTION ? "&LH_Auction=1" : string.Empty)}" +
                    $"{(condition != Condition.NULL ? $"&LH_ItemCondition={GetConditionValue(condition)}" : string.Empty)}";

        return $"{_baseUrl}/sch/i.html?_nkw={HttpUtility.UrlEncode(query)}{flags}" +
               $"&_pgn={page}&_ipg=240&LH_TitleDesc=0";
    }

    public string BuildListingUrl(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            throw new ArgumentException("Item ID cannot be null or empty.", nameof(itemId));

        return $"{_baseUrl}/itm/{HttpUtility.UrlEncode(itemId)}";
    }

    private static int GetConditionValue(Condition condition) => condition switch
    {
        Condition.NEW => 1000,
        Condition.USED => 3000,
        Condition.FOR_PARTS_NOT_WORKING => 7000,
        Condition.GOOD_REFURBISHED => 2030,
        Condition.VERY_GOOD_REFURBISHED => 2020,
        Condition.EXCELLENT_REFURBISHED => 2010,
        Condition.OPENED_NEVER_USED => 1500,
        _ => 0
    };
}
