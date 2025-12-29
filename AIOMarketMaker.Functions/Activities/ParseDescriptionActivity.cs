using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Parsers;
using AngleSharp;

namespace AIOMarketMaker.Functions.Activities;

/// <summary>
/// Parses description page HTML and returns the description text.
/// This activity only parses - it does NOT fetch any HTML.
/// </summary>
public class ParseDescriptionActivity
{
    private readonly IListingParser _listingParser;
    private readonly ILogger<ParseDescriptionActivity> _logger;

    public ParseDescriptionActivity(
        IListingParser listingParser,
        ILogger<ParseDescriptionActivity> logger)
    {
        _listingParser = listingParser;
        _logger = logger;
    }

    [Function(nameof(ParseDescriptionActivity))]
    public async Task<string?> Run(
        [ActivityTrigger] string html,
        FunctionContext context)
    {
        _logger.LogDebug("Parsing description HTML");

        try
        {
            if (string.IsNullOrEmpty(html))
            {
                return null;
            }

            var browsingContext = BrowsingContext.New(Configuration.Default);
            var doc = await browsingContext.OpenAsync(req => req.Content(html));
            var description = _listingParser.ParseDescription(doc);

            _logger.LogDebug("Parsed description: {Length} chars", description?.Length ?? 0);

            return description;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing description");
            return null;
        }
    }
}
