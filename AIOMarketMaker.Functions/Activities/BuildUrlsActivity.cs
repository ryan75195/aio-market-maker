using Microsoft.Azure.Functions.Worker;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Functions.Activities;

/// <summary>
/// Activities for building eBay URLs.
/// These are quick activities that use IEbayUrlBuilder.
/// </summary>
public class BuildUrlsActivity
{
    private readonly IEbayUrlBuilder _urlBuilder;

    public BuildUrlsActivity(IEbayUrlBuilder urlBuilder)
    {
        _urlBuilder = urlBuilder;
    }

    [Function(nameof(BuildSearchUrlActivity))]
    public string BuildSearchUrlActivity(
        [ActivityTrigger] BuildSearchUrlInput input,
        FunctionContext context)
    {
        return _urlBuilder.BuildSearchUrl(
            input.SearchTerm,
            input.IsSold,
            input.Page,
            Condition.NULL,
            BuyingFormat.ALL);
    }

    [Function(nameof(BuildListingUrlActivity))]
    public string BuildListingUrlActivity(
        [ActivityTrigger] string listingId,
        FunctionContext context)
    {
        return _urlBuilder.BuildListingUrl(listingId);
    }
}

public record BuildSearchUrlInput(string SearchTerm, bool IsSold, int Page);
