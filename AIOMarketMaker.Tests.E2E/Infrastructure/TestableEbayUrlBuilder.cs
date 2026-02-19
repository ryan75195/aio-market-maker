using System.Web;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Tests.E2E.Infrastructure;

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

    public string BuildDescriptionUrl(string listingId)
    {
        if (string.IsNullOrWhiteSpace(listingId))
            throw new ArgumentException("Listing ID cannot be null or empty.", nameof(listingId));

        return $"https://itm.ebaydesc.com/itmdesc/{listingId}" +
               "?t=0&category=139971&excSoj=1&ver=0&excTrk=1&lsite=3" +
               "&ittenable=false&domain=ebay.com&descgauge=1&cspheader=1&oneClk=2&secureDesc=1";
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
