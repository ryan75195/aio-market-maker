using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Functions.Contracts;

namespace AIOMarketMaker.Functions.Activities;

public class SaveListingsActivity
{
    private readonly EtlDbContext _dbContext;
    private readonly ILogger<SaveListingsActivity> _logger;

    public SaveListingsActivity(
        EtlDbContext dbContext,
        ILogger<SaveListingsActivity> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [Function(nameof(SaveListingsActivity))]
    public async Task Run(
        [ActivityTrigger] SaveListingsInput input,
        FunctionContext context)
    {
        _logger.LogInformation("Saving {Count} listings for job {JobId}",
            input.Listings.Count, input.JobId);

        var newListings = input.Listings
            .Where(l => !string.IsNullOrEmpty(l.ListingId))
            .Where(l => !string.IsNullOrWhiteSpace(l.Title))
            .Select(l => new Listing
            {
                ListingId = l.ListingId,
                ScrapeJobId = input.JobId,
                Title = l.Title,
                Price = l.Price,
                Currency = l.Currency,
                ShippingCost = l.ShippingCost,
                Url = l.Url,
                Condition = l.Condition,
                ListingStatus = l.ListingStatus,
                PurchaseFormat = l.PurchaseFormat,
                Description = l.Description,
                ItemSpecifics = l.ItemSpecifics,
                Images = l.Images != null ? JsonSerializer.Serialize(l.Images) : null,
                Location = l.Location,
                EndDateUtc = l.EndDateUtc,
                CreatedUtc = DateTime.UtcNow
            })
            .ToList();

        _dbContext.Listings.AddRange(newListings);
        await _dbContext.SaveChangesAsync();

        // Create initial status history records
        var historyRecords = newListings.Select(l => new ListingStatusHistory
        {
            ListingId = l.Id,
            ListingStatus = l.ListingStatus ?? "Unknown",
            Price = l.Price,
            SoldDateUtc = l.EndDateUtc,
            RecordedUtc = DateTime.UtcNow,
            Source = "InitialScrape"
        }).ToList();

        _dbContext.ListingStatusHistory.AddRange(historyRecords);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Saved {Count} listings and history records", newListings.Count);
    }
}
