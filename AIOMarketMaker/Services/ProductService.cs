using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Services.Dtos;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Services;

public class ProductService : IProductService
{
    private readonly EtlDbContext _dbContext;

    public ProductService(EtlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResult<ProductDto>> GetProductsAsync(ProductFilter filter, CancellationToken ct = default)
    {
        var query = _dbContext.Products.AsQueryable();

        if (!string.IsNullOrEmpty(filter.Category))
            query = query.Where(p => p.Category == filter.Category);

        if (!string.IsNullOrEmpty(filter.Brand))
            query = query.Where(p => p.Brand == filter.Brand);

        if (!string.IsNullOrEmpty(filter.Model))
            query = query.Where(p => p.Model != null && p.Model.Contains(filter.Model));

        if (!string.IsNullOrEmpty(filter.ProductName))
            query = query.Where(p => p.ProductName == filter.ProductName);

        if (!string.IsNullOrEmpty(filter.Status))
            query = query.Where(p => p.ListingStatus == filter.Status);

        if (!string.IsNullOrEmpty(filter.Search))
            query = query.Where(p => p.Title != null && p.Title.Contains(filter.Search));

        if (!string.IsNullOrEmpty(filter.Edition))
            query = query.Where(p => p.Edition == filter.Edition);

        if (!string.IsNullOrEmpty(filter.StorageCapacity))
            query = query.Where(p => p.StorageCapacity == filter.StorageCapacity);

        if (!string.IsNullOrEmpty(filter.Color))
            query = query.Where(p => p.Color == filter.Color);

        var total = await query.CountAsync(ct);
        var products = await query
            .OrderByDescending(p => p.Id)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(p => new ProductDto(
                p.Id,
                p.EbayListingId,
                p.ProductName,
                p.Title,
                p.Url,
                p.Price,
                p.Currency,
                p.ShippingCost,
                p.Category,
                p.CategoryConfidence,
                p.Condition,
                p.ListingStatus,
                p.PurchaseFormat,
                p.Brand,
                p.Model,
                p.StorageCapacity,
                p.Color,
                p.Edition,
                p.VariantType,
                p.BundledItems,
                p.Location,
                p.ListedDateUtc,
                p.SoldDateUtc,
                p.EndDateUtc,
                p.ResolvedUtc
            ))
            .ToListAsync(ct);

        return new PagedResult<ProductDto>(total, filter.Page, filter.PageSize, products);
    }

    public async Task<List<ProductNameSummary>> GetProductNamesAsync(string? category, CancellationToken ct = default)
    {
        var query = _dbContext.Products.AsQueryable();

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        return await query
            .Where(p => p.ProductName != null)
            .GroupBy(p => p.ProductName)
            .Select(g => new ProductNameSummary(
                g.Key,
                g.Count(),
                g.Count(p => p.ListingStatus == "Sold"),
                g.Count(p => p.ListingStatus != "Sold"),
                g.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0
            ))
            .OrderByDescending(x => x.Count)
            .ToListAsync(ct);
    }

    public async Task<ProductVariants?> GetProductVariantsAsync(string productName, CancellationToken ct = default)
    {
        var products = await _dbContext.Products
            .Where(p => p.ProductName == productName)
            .ToListAsync(ct);

        if (products.Count == 0)
            return null;

        return new ProductVariants(
            productName,
            products.Count,
            products.Count(p => p.ListingStatus == "Sold"),
            products.Count(p => p.ListingStatus != "Sold"),
            products.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0,
            products
                .Where(p => !string.IsNullOrEmpty(p.Edition))
                .GroupBy(p => p.Edition)
                .Select(g => new VariantBreakdown(
                    g.Key,
                    g.Count(),
                    g.Count(p => p.ListingStatus == "Sold"),
                    g.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0
                ))
                .OrderByDescending(x => x.Count)
                .ToList(),
            products
                .Where(p => !string.IsNullOrEmpty(p.StorageCapacity))
                .GroupBy(p => p.StorageCapacity)
                .Select(g => new VariantBreakdown(
                    g.Key,
                    g.Count(),
                    g.Count(p => p.ListingStatus == "Sold"),
                    g.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0
                ))
                .OrderByDescending(x => x.Count)
                .ToList(),
            products
                .Where(p => !string.IsNullOrEmpty(p.Color))
                .GroupBy(p => p.Color)
                .Select(g => new VariantBreakdown(
                    g.Key,
                    g.Count(),
                    g.Count(p => p.ListingStatus == "Sold"),
                    g.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0
                ))
                .OrderByDescending(x => x.Count)
                .ToList(),
            products
                .Where(p => !string.IsNullOrEmpty(p.Model))
                .GroupBy(p => p.Model)
                .Select(g => new VariantBreakdown(
                    g.Key,
                    g.Count(),
                    g.Count(p => p.ListingStatus == "Sold"),
                    g.Where(p => p.Price.HasValue).Average(p => (double?)p.Price) ?? 0
                ))
                .OrderByDescending(x => x.Count)
                .ToList()
        );
    }
}
