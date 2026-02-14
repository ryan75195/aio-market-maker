using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Pinecone;

namespace AIOMarketMaker.Api.Endpoints;

public record OpportunityListing(
    int Id, string ListingId, string? Title, decimal? Price,
    string? Currency, decimal? ShippingCost, string? Url,
    string? Condition, string? ListingStatus, DateTime? EndDateUtc,
    DateTime CreatedUtc, string? SearchTerm, string? Images,
    decimal? AverageSoldPrice, int SimilarSoldCount,
    int? EstimatedDaysToSell, decimal? PotentialProfit);

public record PricingAggregate(decimal? AvgPrice, int Count, int? AvgDaysToSell);
public record DeletedResponse(int Deleted);
public record ClearListingsResponse(int Deleted, bool IndexCleared);
public record ClearHistoryResponse(int Deleted);
public record ClearDataResponse(int DeletedListings, int DeletedRuns, bool BlobsCleared, bool IndexCleared);
public record ListingStatsEntry(string? Currency, int Total, int NullPrice, int NullTitle);
public record InvalidListingResponse(int Id, string ListingId, string? Title, decimal? Price, string? Currency, string? Url, DateTime CreatedUtc);

public static class ListingEndpoints
{
    public static void MapListingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/listings/active", GetActiveListings);
        app.MapGet("/api/listings/stats", GetListingStats);
        app.MapGet("/api/listings/invalid", GetInvalidListings);
        app.MapDelete("/api/listings/invalid", DeleteInvalidListings);
        app.MapDelete("/api/listings/all", ClearAllListings);
        app.MapDelete("/api/history/all", ClearAllHistory);
        app.MapDelete("/api/data/all", ClearAllData);
    }

    private static async Task<IResult> GetActiveListings(EtlDbContext db)
    {
        // Load predictions for active listings
        var predictions = await db.ListingPredictions
            .Where(p => db.Listings.Any(l => l.Id == p.ListingId && l.ListingStatus == "Active"))
            .ToDictionaryAsync(
                p => p.ListingId,
                p => new PricingAggregate(p.AverageSoldPrice, p.SimilarSoldCount, p.EstimatedDaysToSell));

        var enrichedListings = await db.Listings
            .Include(l => l.ScrapeJob)
            .Where(l => l.ListingStatus == "Active" && predictions.Keys.Contains(l.Id))
            .ToListAsync();

        var enrichedResults = enrichedListings
            .Select(l => ToOpportunityListing(l, predictions))
            .OrderByDescending(o => o.PotentialProfit ?? decimal.MinValue)
            .Take(100)
            .ToList();

        // Fill remaining slots with active listings that have no comparables
        if (enrichedResults.Count < 100)
        {
            var enrichedIds = enrichedResults.Select(r => r.Id).ToHashSet();
            var remaining = await db.Listings
                .Include(l => l.ScrapeJob)
                .Where(l => l.ListingStatus == "Active" && !enrichedIds.Contains(l.Id))
                .OrderByDescending(l => l.CreatedUtc)
                .Take(100 - enrichedResults.Count)
                .ToListAsync();

            enrichedResults.AddRange(remaining.Select(l => ToOpportunityListing(l, predictions)));
        }

        return Results.Ok(enrichedResults);
    }

    private static async Task<IResult> GetListingStats(EtlDbContext db)
    {
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);
        var stats = await db.Listings
            .Where(l => l.CreatedUtc > twoHoursAgo)
            .GroupBy(l => l.Currency)
            .Select(g => new ListingStatsEntry(
                g.Key,
                g.Count(),
                g.Count(x => x.Price == null),
                g.Count(x => x.Title == null)))
            .ToListAsync();

        return Results.Ok(stats);
    }

    private static async Task<IResult> GetInvalidListings(EtlDbContext db)
    {
        var invalidListings = await db.Listings
            .Where(l => l.Title == null || l.Price == null)
            .OrderByDescending(l => l.CreatedUtc)
            .Take(100)
            .Select(l => new InvalidListingResponse(
                l.Id, l.ListingId, l.Title, l.Price,
                l.Currency, l.Url, l.CreatedUtc))
            .ToListAsync();

        return Results.Ok(invalidListings);
    }

    private static async Task<IResult> DeleteInvalidListings(
        EtlDbContext db, ILogger<Program> logger)
    {
        var invalidListings = await db.Listings
            .Where(l => l.Title == null || l.Price == null)
            .ToListAsync();

        var count = invalidListings.Count;
        db.Listings.RemoveRange(invalidListings);
        await db.SaveChangesAsync();

        logger.LogInformation("Deleted {Count} invalid listings (missing title or price)", count);

        return Results.Ok(new DeletedResponse(count));
    }

    private static async Task<IResult> ClearAllListings(
        EtlDbContext db, IPineconeIndexClient pineconeClient, ILogger<Program> logger)
    {
        var count = await db.Listings.CountAsync();

        if (count > 0)
        {
            // Delete relationships first (NoAction FK to Listings). Predictions are a live view.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ListingRelationships");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
            logger.LogInformation("Cleared {Count} listings from database", count);
        }

        bool indexCleared = await ClearPineconeIndex(pineconeClient, logger);

        return Results.Ok(new ClearListingsResponse(count, indexCleared));
    }

    private static async Task<IResult> ClearAllHistory(
        EtlDbContext db, ILogger<Program> logger)
    {
        var count = await db.ScrapeRuns.CountAsync();

        if (count > 0)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");
            logger.LogInformation("Cleared {Count} scrape runs from database", count);
        }

        return Results.Ok(new ClearHistoryResponse(count));
    }

    private static async Task<IResult> ClearAllData(
        EtlDbContext db, BlobServiceClient blobService, IPineconeIndexClient pineconeClient,
        ILogger<Program> logger)
    {
        var listingsCount = await db.Listings.CountAsync();
        var runsCount = await db.ScrapeRuns.CountAsync();

        // Delete in correct order: relationships first (NoAction FK), then Listings, then ScrapeRuns (cascades)
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ListingRelationships");
        if (listingsCount > 0)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
        }
        if (runsCount > 0)
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ScrapeRuns");
        }

        // Clear blob storage (HTML files) - delete and recreate container for speed
        bool blobsCleared = false;
        try
        {
            var containerClient = blobService.GetBlobContainerClient("html");
            if (await containerClient.ExistsAsync())
            {
                await containerClient.DeleteAsync();
                await containerClient.CreateIfNotExistsAsync();
                blobsCleared = true;
            }
            logger.LogInformation("Cleared html blob container");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear blob storage (non-fatal)");
        }

        // Clear Pinecone vector index
        bool indexCleared = await ClearPineconeIndex(pineconeClient, logger);

        logger.LogInformation(
            "Cleared all data: {Listings} listings, {Runs} scrape runs, blobs cleared: {BlobsCleared}, index cleared: {IndexCleared}",
            listingsCount, runsCount, blobsCleared, indexCleared);

        return Results.Ok(new ClearDataResponse(listingsCount, runsCount, blobsCleared, indexCleared));
    }

    private static async Task<bool> ClearPineconeIndex(
        IPineconeIndexClient pineconeClient, ILogger logger)
    {
        try
        {
            await pineconeClient.Delete(new DeleteRequest { DeleteAll = true });
            logger.LogInformation("Cleared Pinecone vector index");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear Pinecone index (non-fatal)");
            return false;
        }
    }

    private static OpportunityListing ToOpportunityListing(
        Listing l, Dictionary<int, PricingAggregate> grouped)
    {
        grouped.TryGetValue(l.Id, out var agg);

        return new OpportunityListing(
            l.Id,
            l.ListingId,
            l.Title,
            l.Price,
            l.Currency,
            l.ShippingCost,
            l.Url,
            l.Condition,
            l.ListingStatus,
            l.EndDateUtc,
            l.CreatedUtc,
            l.ScrapeJob?.SearchTerm,
            l.Images,
            agg?.AvgPrice,
            agg?.Count ?? 0,
            agg?.AvgDaysToSell,
            agg?.AvgPrice != null && l.Price.HasValue
                ? agg.AvgPrice.Value - l.Price.Value
                : null);
    }
}
