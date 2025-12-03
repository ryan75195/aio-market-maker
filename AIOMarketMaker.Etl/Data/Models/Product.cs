using AIOMarketMaker.Core.Models;

namespace AIOMarketMaker.Etl.Data.Models;

/// <summary>
/// Denormalized product data combining LLM classification with listing details.
/// </summary>
public class Product : IProductInfo
{
    public int Id { get; set; }

    // === Core Identification ===
    public string? EbayListingId { get; set; }
    public string? ProductName { get; set; }
    public string? Title { get; set; }
    public string? Url { get; set; }

    // === Pricing ===
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public decimal? ShippingCost { get; set; }

    // === Classification ===
    public required string Category { get; set; }
    public decimal? CategoryConfidence { get; set; }
    public string? Condition { get; set; }
    public string? ListingStatus { get; set; }
    public string? PurchaseFormat { get; set; }

    // === Product Attributes (LLM-normalized) ===
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? StorageCapacity { get; set; }
    public string? Color { get; set; }
    public string? Edition { get; set; }
    public string? VariantType { get; set; }
    public string? BundledItems { get; set; }

    // === Location ===
    public string? Location { get; set; }

    // === Dates ===
    public DateTime? ListedDateUtc { get; set; }
    public DateTime? SoldDateUtc { get; set; }
    public DateTime? EndDateUtc { get; set; }
    public DateTime ResolvedUtc { get; set; } = DateTime.UtcNow;

    // === Foreign Keys ===
    public int ListingId { get; set; }

    // === Navigation ===
    public Listing Listing { get; set; } = null!;
}
