using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
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
        _logger.LogInformation("Upserting {Count} listings for job {JobId}",
            input.Listings.Count, input.JobId);

        var validListings = input.Listings
            .Where(l => !string.IsNullOrEmpty(l.ListingId))
            .Where(l => !string.IsNullOrWhiteSpace(l.Title))
            .ToList();

        var insertCount = 0;
        var updateCount = 0;

        foreach (var listingData in validListings)
        {
            var existing = await _dbContext.Listings
                .FirstOrDefaultAsync(l => l.ListingId == listingData.ListingId);

            if (existing == null)
            {
                // INSERT new listing
                var newListing = MapToListing(listingData, input.JobId);
                _dbContext.Listings.Add(newListing);
                await _dbContext.SaveChangesAsync();

                // Create initial history record
                _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                {
                    ListingId = newListing.Id,
                    ListingStatus = newListing.ListingStatus ?? "Unknown",
                    Price = newListing.Price,
                    SoldDateUtc = newListing.EndDateUtc,
                    RecordedUtc = DateTime.UtcNow,
                    Source = "InitialScrape"
                });
                insertCount++;
            }
            else
            {
                // UPDATE existing listing with status protection
                var statusChanged = UpdateExistingListing(existing, listingData);

                if (statusChanged)
                {
                    // Add history record for status change
                    _dbContext.ListingStatusHistory.Add(new ListingStatusHistory
                    {
                        ListingId = existing.Id,
                        ListingStatus = existing.ListingStatus ?? "Unknown",
                        Price = existing.Price,
                        SoldDateUtc = existing.EndDateUtc,
                        RecordedUtc = DateTime.UtcNow,
                        Source = "StatusUpdate"
                    });
                }
                updateCount++;
            }
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Upserted listings: {Inserted} inserted, {Updated} updated",
            insertCount, updateCount);
    }

    private static Listing MapToListing(ListingData data, int jobId)
    {
        return new Listing
        {
            ListingId = data.ListingId,
            ScrapeJobId = jobId,
            Title = data.Title,
            Price = data.Price,
            Currency = data.Currency,
            ShippingCost = data.ShippingCost,
            Url = data.Url,
            Condition = data.Condition,
            ListingStatus = data.ListingStatus,
            PurchaseFormat = data.PurchaseFormat,
            Description = data.Description,
            ItemSpecifics = data.ItemSpecifics,
            Images = data.Images != null ? JsonSerializer.Serialize(data.Images) : null,
            Location = data.Location,
            EndDateUtc = data.EndDateUtc,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static bool UpdateExistingListing(Listing existing, ListingData data)
    {
        var statusChanged = false;

        // Only update status if it's a forward progression
        if (ListingStatusHelper.CanUpdateStatus(existing.ListingStatus, data.ListingStatus))
        {
            if (existing.ListingStatus != data.ListingStatus)
            {
                existing.ListingStatus = data.ListingStatus;
                existing.EndDateUtc = data.EndDateUtc;
                statusChanged = true;
            }
        }

        // Always update data fields (don't touch CreatedUtc or ScrapeJobId)
        existing.Title = data.Title;
        existing.Price = data.Price;
        existing.Currency = data.Currency;
        existing.ShippingCost = data.ShippingCost;
        existing.Url = data.Url;
        existing.Condition = data.Condition;
        existing.PurchaseFormat = data.PurchaseFormat;
        existing.Description = data.Description;
        existing.ItemSpecifics = data.ItemSpecifics;
        existing.Location = data.Location;
        if (data.Images != null)
        {
            existing.Images = JsonSerializer.Serialize(data.Images);
        }
        existing.UpdatedUtc = DateTime.UtcNow;

        return statusChanged;
    }
}
