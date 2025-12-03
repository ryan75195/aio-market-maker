using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Services.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Services;

public class ListingService : IListingService
{
    private readonly EtlDbContext _dbContext;

    public ListingService(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResult<ListingDto>> GetListingsAsync(ListingFilter filter, CancellationToken ct = default)
    {
        var query = _dbContext.Listings.AsQueryable();

        if (!string.IsNullOrEmpty(filter.Status))
            query = query.Where(x => x.ListingStatus == filter.Status);

        if (filter.JobId.HasValue)
            query = query.Where(x => x.ScrapeJobId == filter.JobId.Value);

        if (!string.IsNullOrEmpty(filter.Search))
            query = query.Where(x => x.Title != null && x.Title.Contains(filter.Search));

        var total = await query.CountAsync(ct);
        var listings = await query
            .OrderByDescending(x => x.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(x => new ListingDto(
                x.Id,
                x.ListingId,
                x.Title,
                x.Price,
                x.Currency,
                x.ListingStatus,
                x.Condition,
                x.Url,
                x.EndDateUtc,
                x.CreatedUtc,
                x.ScrapeJobId
            ))
            .ToListAsync(ct);

        return new PagedResult<ListingDto>(total, filter.Page, filter.PageSize, listings);
    }

    public async Task<ListingDetails?> GetListingDetailsAsync(int id, CancellationToken ct = default)
    {
        var listing = await _dbContext.Listings
            .Include(l => l.StatusHistory)
            .Include(l => l.ScrapeJob)
            .Include(l => l.Product)
            .FirstOrDefaultAsync(l => l.Id == id, ct);

        if (listing == null)
            return null;

        var history = listing.StatusHistory
            .OrderByDescending(h => h.RecordedUtc)
            .Select(h => new StatusHistoryDto(
                h.Id,
                h.ListingStatus,
                h.Price,
                h.SoldDateUtc,
                h.RecordedUtc,
                h.Source
            ))
            .ToList();

        var listingDto = new ListingFullDto(
            listing.Id,
            listing.ListingId,
            listing.Title,
            listing.Price,
            listing.Currency,
            listing.ShippingCost,
            listing.Condition,
            listing.ListingStatus,
            listing.PurchaseFormat,
            listing.Description,
            listing.ItemSpecifics,
            listing.Images,
            listing.Location,
            listing.Url,
            listing.EndDateUtc,
            listing.CreatedUtc,
            listing.UpdatedUtc,
            listing.ScrapeJob != null ? new JobSummaryDto(listing.ScrapeJob.Id, listing.ScrapeJob.SearchTerm) : null
        );

        var productDto = listing.Product != null ? new ProductSummaryDto(
            listing.Product.Id,
            listing.Product.Category,
            listing.Product.CategoryConfidence,
            listing.Product.Brand,
            listing.Product.Model,
            listing.Product.StorageCapacity,
            listing.Product.Color,
            listing.Product.Edition,
            listing.Product.VariantType,
            listing.Product.BundledItems,
            listing.Product.ListedDateUtc,
            listing.Product.SoldDateUtc,
            listing.Product.ResolvedUtc
        ) : null;

        return new ListingDetails(listingDto, productDto, history);
    }
}
