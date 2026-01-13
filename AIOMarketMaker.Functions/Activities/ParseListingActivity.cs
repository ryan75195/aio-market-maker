using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Functions.Contracts;
using AngleSharp;

namespace AIOMarketMaker.Functions.Activities;

/// <summary>
/// Parses listing page HTML and returns parsed data.
/// This activity only parses - it does NOT fetch any HTML.
/// </summary>
public class ParseListingActivity
{
    private readonly IListingParser _listingParser;
    private readonly ILogger<ParseListingActivity> _logger;

    public ParseListingActivity(
        IListingParser listingParser,
        ILogger<ParseListingActivity> logger)
    {
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function(nameof(ParseListingActivity))]
    public async Task<ParsedListingResult?> Run(
        [ActivityTrigger] ParseListingInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Parsing listing {ListingId}", input.ListingId);

        try
        {
            if (string.IsNullOrEmpty(input.Html))
            {
                _logger.LogWarning("Empty HTML for listing {ListingId}", input.ListingId);
                return null;
            }

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var doc = await browsingContext.OpenAsync(req => req.Content(input.Html));
            var parsed = _listingParser.ParseProductListing(doc, input.ListingUrl);

            _logger.LogInformation("Parsed listing {ListingId}: {Title}", input.ListingId, parsed.title);

            return new ParsedListingResult(
                ListingId: parsed.id ?? input.ListingId,
                Title: parsed.title,
                Price: parsed.price,
                Currency: parsed.currency,
                ShippingCost: parsed.shippingCost,
                Condition: parsed.Condition?.ToString(),
                ListingStatus: parsed.listingStatus?.ToString(),
                PurchaseFormat: parsed.purchaseFormat?.ToString(),
                Url: parsed.Url,
                EndDateUtc: parsed.SoldDateUtc,
                Location: parsed.Location,
                ItemSpecifics: parsed.ItemSpecifics,
                Images: parsed.images?.ToList(),
                DescriptionSourceUrl: parsed.descriptionSource
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing listing {ListingId}", input.ListingId);
            return null;
        }
    }
}

public record ParseListingInput(string ListingId, string ListingUrl, string Html);

public record ParsedListingResult(
    string ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Condition,
    string? ListingStatus,
    string? PurchaseFormat,
    string? Url,
    DateTime? EndDateUtc,
    string? Location,
    string? ItemSpecifics,
    List<string>? Images,
    string? DescriptionSourceUrl
);
