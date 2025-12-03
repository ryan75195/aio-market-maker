namespace AIOMarketMaker.Services.Dtos;

public record ListingFilter(
    int Page = 1,
    int PageSize = 50,
    string? Status = null,
    int? JobId = null,
    string? Search = null
);

public record ListingDto(
    int Id,
    string? ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    string? ListingStatus,
    string? Condition,
    string? Url,
    DateTime? EndDateUtc,
    DateTime? CreatedUtc,
    int? ScrapeJobId
);

public record ListingDetails(
    ListingFullDto Listing,
    ProductSummaryDto? Product,
    IReadOnlyList<StatusHistoryDto> History
);

public record ListingFullDto(
    int Id,
    string? ListingId,
    string? Title,
    decimal? Price,
    string? Currency,
    decimal? ShippingCost,
    string? Condition,
    string? ListingStatus,
    string? PurchaseFormat,
    string? Description,
    string? ItemSpecifics,
    string? Images,
    string? Location,
    string? Url,
    DateTime? EndDateUtc,
    DateTime? CreatedUtc,
    DateTime? UpdatedUtc,
    JobSummaryDto? Job
);

public record JobSummaryDto(int Id, string? SearchTerm);

public record ProductSummaryDto(
    int Id,
    string? Category,
    decimal? CategoryConfidence,
    string? Brand,
    string? Model,
    string? StorageCapacity,
    string? Color,
    string? Edition,
    string? VariantType,
    string? BundledItems,
    DateTime? ListedDateUtc,
    DateTime? SoldDateUtc,
    DateTime? ResolvedUtc
);

public record StatusHistoryDto(
    int Id,
    string? ListingStatus,
    decimal? Price,
    DateTime? SoldDateUtc,
    DateTime? RecordedUtc,
    string? Source
);
