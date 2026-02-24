using Microsoft.EntityFrameworkCore;
using Azure.Storage.Blobs;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Api.Endpoints;

public record OpportunityListing(
    int Id, string ListingId, string? Title, decimal? Price,
    string? Currency, decimal? ShippingCost, string? Url,
    string? Condition, string? ListingStatus, DateTime? EndDateUtc,
    DateTime CreatedUtc, string? SearchTerm, string? Images,
    decimal? AverageSoldPrice, int SimilarSoldCount,
    int? EstimatedDaysToSell, decimal? PotentialProfit);

public record PagedResponse<T>(
    IEnumerable<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);

public record PricingAggregate(decimal? AvgPrice, int Count, int? AvgDaysToSell);
public record DeletedResponse(int Deleted);
public record ClearListingsResponse(int Deleted, bool IndexCleared);
public record ClearHistoryResponse(int Deleted);
public record ClearDataResponse(int DeletedListings, int DeletedRuns, bool BlobsCleared, bool IndexCleared);
public record ListingStatsEntry(string? Currency, int Total, int NullPrice, int NullTitle);
public record InvalidListingResponse(int Id, string ListingId, string? Title, decimal? Price, string? Currency, string? Url, DateTime CreatedUtc);

public record ListingDetail(
    int Id, string ListingId, string? Title, string? Description,
    decimal? Price, string? Currency, decimal? ShippingCost,
    string? Condition, string? Url, string? Images,
    string? ListingStatus, string? SearchTerm,
    DateTime CreatedUtc,
    decimal? AverageSoldPrice, int SimilarSoldCount,
    int? EstimatedDaysToSell, decimal? PotentialProfit,
    double Confidence = 0, int OutliersRemoved = 0,
    decimal? MedianSoldPrice = null);

public record ComparableListing(
    int RelationshipId, string ListingId, string? Title,
    string? Description, decimal? Price, string? Condition,
    string? Url, string? Images,
    DateTime? SoldDateUtc, double SimilarityScore,
    double? ClassifierConfidence, string Explanation);

public record ListingDetailResponse(
    ListingDetail Listing, IEnumerable<ComparableListing> Comparables);

public static class ListingEndpoints
{
    public static void MapListingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/listings/active", GetActiveListings);
        app.MapGet("/api/listings/stats", GetListingStats);
        app.MapGet("/api/listings/invalid", GetInvalidListings);
        app.MapDelete("/api/listings/invalid", DeleteInvalidListings);
        app.MapGet("/api/listings/{id:int}", GetListingDetail);
        app.MapDelete("/api/listings/{id:int}/comparables/{relationshipId:int}", DismissComparable);
        app.MapDelete("/api/listings/all", ClearAllListings);
        app.MapDelete("/api/history/all", ClearAllHistory);
        app.MapDelete("/api/data/all", ClearAllData);
    }

    private static async Task<IResult> GetActiveListings(
        EtlDbContext db,
        IListingPredictionService predictionService,
        ISemanticSearchService semanticSearchService,
        int page = 1,
        int pageSize = 50,
        string sortBy = "potentialProfit",
        string sortDir = "desc",
        string? jobIds = null,
        int minComps = 0,
        decimal priceBand = 0,
        decimal feePercent = 0,
        bool matchCondition = true,
        decimal maxPrice = 0,
        string? searchQuery = null)
    {
        var filters = new PredictionFilters(priceBand, feePercent, matchCondition, minComps, maxPrice);
        var jobIdList = ParseJobIds(jobIds);

        List<int>? searchListingIds = null;
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchResult = await semanticSearchService.Search(searchQuery, 5000);
            var matchedEbayIds = searchResult.Hits.Select(h => h.ListingId).ToHashSet();
            searchListingIds = await db.Listings
                .Where(l => l.ListingStatus == "Active" && matchedEbayIds.Contains(l.ListingId))
                .Select(l => l.Id)
                .ToListAsync();

            if (searchListingIds.Count == 0)
            {
                return Results.Ok(new PagedResponse<OpportunityListing>(
                    Enumerable.Empty<OpportunityListing>(), 0, page, pageSize, 0));
            }
        }

        var paged = await predictionService.GetPredictions(
            filters, jobIdList.Count > 0 ? jobIdList : null,
            sortBy, sortDir, page, pageSize, searchListingIds);

        if (paged.TotalCount == 0)
        {
            return Results.Ok(new PagedResponse<OpportunityListing>(
                Enumerable.Empty<OpportunityListing>(), 0, page, pageSize, 0));
        }

        var ids = paged.OrderedListingIds.ToList();
        var predictions = paged.Items
            .Where(r => r.AverageSoldPrice > 0)
            .ToDictionary(
                r => r.ListingId,
                r => new PricingAggregate(r.AverageSoldPrice, r.SimilarSoldCount, r.EstimatedDaysToSell));

        Dictionary<int, decimal>? profitOverrides = null;
        if (feePercent > 0 || priceBand > 0)
        {
            profitOverrides = paged.Items
                .Where(r => r.PotentialProfit != 0)
                .ToDictionary(r => r.ListingId, r => r.PotentialProfit);
        }

        var listings = await db.Listings
            .Include(l => l.ScrapeJob)
            .Where(l => ids.Contains(l.Id))
            .ToListAsync();

        var idOrder = ids.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        listings.Sort((a, b) => idOrder.GetValueOrDefault(a.Id, int.MaxValue)
            .CompareTo(idOrder.GetValueOrDefault(b.Id, int.MaxValue)));

        var items = listings.Select(l => ToOpportunityListing(l, predictions, profitOverrides));

        return Results.Ok(new PagedResponse<OpportunityListing>(
            items, paged.TotalCount, paged.Page, paged.PageSize, paged.TotalPages));
    }

    private static List<int> ParseJobIds(string? jobIds)
    {
        if (string.IsNullOrWhiteSpace(jobIds))
        {
            return new List<int>();
        }

        return jobIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
    }

    private static async Task<IResult> GetListingDetail(
        EtlDbContext db, IListingPredictionService predictionService, int id,
        decimal priceBand = 0, decimal feePercent = 0, bool matchCondition = true)
    {
        var listing = await db.Listings
            .Include(l => l.ScrapeJob)
            .FirstOrDefaultAsync(l => l.Id == id);

        if (listing == null)
        {
            return Results.NotFound();
        }

        var filters = new PredictionFilters(priceBand, feePercent, matchCondition);
        var comps = await predictionService.GetComparables(id, filters);
        var prediction = await predictionService.GetPrediction(id, filters);

        var comparables = comps.Select(c => new ComparableListing(
            c.RelationshipId, c.ListingId!, c.Title,
            c.Description, c.Price, c.Condition,
            c.Url, c.Images,
            c.SoldDateUtc, c.SimilarityScore, c.ClassifierConfidence,
            c.Explanation));

        var detail = new ListingDetail(
            listing.Id, listing.ListingId, listing.Title, listing.Description,
            listing.Price, listing.Currency, listing.ShippingCost,
            listing.Condition, listing.Url, listing.Images,
            listing.ListingStatus, listing.ScrapeJob?.SearchTerm,
            listing.CreatedUtc,
            prediction?.AverageSoldPrice, prediction?.SimilarSoldCount ?? 0,
            prediction?.EstimatedDaysToSell, prediction?.PotentialProfit,
            prediction?.Confidence ?? 0, prediction?.OutliersRemoved ?? 0,
            prediction?.MedianSoldPrice);

        return Results.Ok(new ListingDetailResponse(detail, comparables));
    }

    private static async Task<IResult> DismissComparable(
        EtlDbContext db, IListingPredictionService predictionService,
        int id, int relationshipId,
        decimal priceBand = 0, decimal feePercent = 0, bool matchCondition = true)
    {
        var relationship = await db.ListingRelationships
            .FirstOrDefaultAsync(r =>
                r.Id == relationshipId &&
                (r.ListingIdA == id || r.ListingIdB == id));

        if (relationship == null)
        {
            return Results.NotFound();
        }

        db.ListingRelationships.Remove(relationship);
        await db.SaveChangesAsync();

        return await GetListingDetail(db, predictionService, id, priceBand, feePercent, matchCondition);
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
        EtlDbContext db, IVectorIndex vectorIndex, ILogger<Program> logger)
    {
        var count = await db.Listings.CountAsync();

        if (count > 0)
        {
            // Delete relationships first (NoAction FK to Listings). Predictions are a live view.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM ListingRelationships");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM Listings");
            logger.LogInformation("Cleared {Count} listings from database", count);
        }

        bool indexCleared = ClearVectorIndex(vectorIndex, logger);

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
        EtlDbContext db, BlobServiceClient blobService, IVectorIndex vectorIndex,
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

        // Clear local vector index
        bool indexCleared = ClearVectorIndex(vectorIndex, logger);

        logger.LogInformation(
            "Cleared all data: {Listings} listings, {Runs} scrape runs, blobs cleared: {BlobsCleared}, index cleared: {IndexCleared}",
            listingsCount, runsCount, blobsCleared, indexCleared);

        return Results.Ok(new ClearDataResponse(listingsCount, runsCount, blobsCleared, indexCleared));
    }

    private static bool ClearVectorIndex(IVectorIndex vectorIndex, ILogger logger)
    {
        try
        {
            var count = vectorIndex.Count;
            vectorIndex.Clear();
            vectorIndex.Save();
            logger.LogInformation("Cleared {Count} vectors from local index", count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear vector index (non-fatal)");
            return false;
        }
    }

    private static OpportunityListing ToOpportunityListing(
        Listing l,
        Dictionary<int, PricingAggregate> grouped,
        Dictionary<int, decimal>? profitOverrides = null)
    {
        grouped.TryGetValue(l.Id, out var agg);

        decimal? profit = null;
        if (profitOverrides != null && profitOverrides.TryGetValue(l.Id, out var overrideProfit))
        {
            profit = overrideProfit;
        }
        else if (agg?.AvgPrice != null && l.Price.HasValue)
        {
            profit = agg.AvgPrice.Value - l.Price.Value;
        }

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
            profit);
    }
}
