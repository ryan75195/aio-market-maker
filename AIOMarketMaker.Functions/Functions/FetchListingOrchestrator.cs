using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Functions.Activities;

namespace AIOMarketMaker.Functions.Functions;

/// <summary>
/// Sub-orchestrator that fetches a single listing and its description.
/// Uses ScrapeUrlOrchestrator for the actual fetching (with durable timers).
/// </summary>
public class FetchListingOrchestrator
{
    [Function(nameof(FetchListingOrchestrator))]
    public async Task<ListingData?> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var logger = context.CreateReplaySafeLogger<FetchListingOrchestrator>();
        var input = context.GetInput<FetchListingInput>()!;

        logger.LogInformation("Fetching listing {ListingId}", input.ListingId);

        try
        {
            // Step 1: Scrape the listing page
            var listingHtml = await context.CallSubOrchestratorAsync<string?>(
                nameof(ScrapeUrlOrchestrator), input.ListingUrl);

            if (string.IsNullOrEmpty(listingHtml))
            {
                logger.LogWarning("Empty HTML for listing {ListingId}", input.ListingId);
                return null;
            }

            // Step 2: Parse the listing page
            var parsed = await context.CallActivityAsync<ParsedListingResult?>(
                nameof(ParseListingActivity),
                new ParseListingInput(input.ListingId, input.ListingUrl, listingHtml));

            if (parsed == null)
            {
                logger.LogWarning("Failed to parse listing {ListingId}", input.ListingId);
                return null;
            }

            // Step 3: Scrape and parse description if available
            string? description = null;
            if (!string.IsNullOrEmpty(parsed.DescriptionSourceUrl))
            {
                try
                {
                    var descHtml = await context.CallSubOrchestratorAsync<string?>(
                        nameof(ScrapeUrlOrchestrator), parsed.DescriptionSourceUrl);

                    if (!string.IsNullOrEmpty(descHtml))
                    {
                        description = await context.CallActivityAsync<string?>(
                            nameof(ParseDescriptionActivity), descHtml);
                    }
                }
                catch (Exception ex)
                {
                    // Don't fail the whole listing if description fails
                    logger.LogWarning(ex, "Failed to fetch description for {ListingId}", input.ListingId);
                }
            }

            logger.LogInformation("Successfully fetched listing {ListingId}", input.ListingId);

            return new ListingData(
                ListingId: parsed.ListingId,
                Title: parsed.Title,
                Price: parsed.Price,
                Currency: parsed.Currency,
                ShippingCost: parsed.ShippingCost,
                Condition: parsed.Condition,
                ListingStatus: parsed.ListingStatus,
                PurchaseFormat: parsed.PurchaseFormat,
                Description: description,
                Url: parsed.Url,
                EndDateUtc: parsed.EndDateUtc,
                Location: parsed.Location,
                ItemSpecifics: parsed.ItemSpecifics,
                Images: parsed.Images
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch listing {ListingId}", input.ListingId);
            return null;
        }
    }
}

public record FetchListingInput(string ListingId, string ListingUrl);
