namespace AIOMarketMaker.Etl.Services.EntityResolution;

/// <summary>
/// Result of entity resolution for a single listing.
/// </summary>
public record EntityResolutionResult(
    string ListingId,
    string Category,
    decimal? CategoryConfidence,
    string? ProductName,
    NormalizedAttributes Attributes,
    string[]? BundledItems
);

/// <summary>
/// Normalized product attributes extracted by the LLM.
/// </summary>
public record NormalizedAttributes(
    string? Brand,
    string? Model,
    string? StorageCapacity,
    string? Color,
    string? Edition,
    string? VariantType
);

/// <summary>
/// Abstract product categories that apply to any domain.
/// </summary>
public static class ProductCategory
{
    public const string BaseProduct = "base_product";
    public const string Bundle = "bundle";
    public const string Accessory = "accessory";
    public const string Consumable = "consumable";
    public const string ReplacementPart = "replacement_part";
    public const string PackagingOnly = "packaging_only";
    public const string Media = "media";
    public const string Other = "other";

    public static readonly string[] All =
    [
        BaseProduct,
        Bundle,
        Accessory,
        Consumable,
        ReplacementPart,
        PackagingOnly,
        Media,
        Other
    ];
}
