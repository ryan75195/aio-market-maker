namespace AIOMarketMaker.Services.Dtos;

public record ProductFilter(
    int Page = 1,
    int PageSize = 50,
    string? Category = null,
    string? Brand = null,
    string? Model = null,
    string? ProductName = null,
    string? Status = null,
    string? Search = null,
    string? Edition = null,
    string? StorageCapacity = null,
    string? Color = null
);

public record ProductDto(
    int Id,
    string? EbayListingId,
    string? ProductName,
    string? Title,
    string? Url,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Category,
    decimal? CategoryConfidence,
    string? Condition,
    string? ListingStatus,
    string? PurchaseFormat,
    string? Brand,
    string? Model,
    string? StorageCapacity,
    string? Color,
    string? Edition,
    string? VariantType,
    string? BundledItems,
    string? Location,
    DateTime? ListedDateUtc,
    DateTime? SoldDateUtc,
    DateTime? EndDateUtc,
    DateTime? ResolvedUtc
);

public record ProductVariants(
    string ProductName,
    int TotalCount,
    int SoldCount,
    int ActiveCount,
    double AvgPrice,
    IReadOnlyList<VariantBreakdown> Editions,
    IReadOnlyList<VariantBreakdown> StorageCapacities,
    IReadOnlyList<VariantBreakdown> Colors,
    IReadOnlyList<VariantBreakdown> Models
);

public record VariantBreakdown(
    string? Value,
    int Count,
    int SoldCount,
    double AvgPrice
);

public record ProductNameSummary(
    string? ProductName,
    int Count,
    int SoldCount,
    int ActiveCount,
    double AvgPrice
);
