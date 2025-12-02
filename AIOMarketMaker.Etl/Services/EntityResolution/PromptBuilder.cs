using System.Text;
using System.Text.Json;
using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Etl.Services.EntityResolution;

/// <summary>
/// Builds prompts for entity resolution using an LLM.
/// </summary>
public class PromptBuilder
{
    private const int MaxDescriptionLength = 500;

    public string SystemPrompt => """
        You are a product classification expert. Your task is to analyze product listings and:
        1. Classify each product into an absolute category (what the item IS, not whether it matches a search)
        2. Extract and normalize key product attributes

        You must respond with valid JSON following the exact schema provided.
        Be precise and consistent in your classifications.
        """;

    public string BuildUserPrompt(IReadOnlyList<EbayProduct> products)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Classification Categories (Absolute - What the item IS)");
        sb.AppendLine();
        sb.AppendLine("- base_product: The primary/standalone product (console, phone, laptop, appliance, etc.)");
        sb.AppendLine("- bundle: Multiple items sold together as a package");
        sb.AppendLine("- accessory: Add-on item that works with a base product (case, controller, charger, etc.)");
        sb.AppendLine("- consumable: Items that get used up (ink, batteries, filters, cleaning supplies, etc.)");
        sb.AppendLine("- replacement_part: Spare parts, repair components");
        sb.AppendLine("- packaging_only: Empty box, case, or packaging without the actual product");
        sb.AppendLine("- media: Software, games, movies, music, books");
        sb.AppendLine("- other: Doesn't fit other categories");
        sb.AppendLine();
        sb.AppendLine("## Products to Classify");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(SerializeProducts(products));
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Required Response Format");
        sb.AppendLine();
        sb.AppendLine("Respond with a JSON object containing a 'results' array with one object per product:");
        sb.AppendLine("```json");
        sb.AppendLine("""
            {
              "results": [
                {
                  "listingId": "123456789",
                  "category": "base_product",
                  "confidence": 0.95,
                  "productName": "PlayStation 5",
                  "attributes": {
                    "brand": "Sony",
                    "model": "PlayStation 5",
                    "storageCapacity": "825GB",
                    "color": "White",
                    "edition": "Disc Edition",
                    "variantType": null
                  },
                  "bundledItems": null
                }
              ]
            }
            """);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"Classify all {products.Count} products. Be thorough and accurate.");
        sb.AppendLine("For bundles, list bundledItems as an array of item descriptions.");
        sb.AppendLine("Set attributes to null if not determinable from the listing.");
        sb.AppendLine("productName should be the canonical product name (e.g., 'PlayStation 5', 'DualSense Controller', 'iPhone 15 Pro').");

        return sb.ToString();
    }

    private string SerializeProducts(IReadOnlyList<EbayProduct> products)
    {
        var simplified = products.Select(p => new
        {
            listingId = p.ListingId,
            title = p.Title,
            price = p.Price,
            condition = p.Condition?.ToString(),
            itemSpecifics = TruncateString(p.ItemSpecifics, 1000),
            description = TruncateString(p.Description, MaxDescriptionLength)
        });

        return JsonSerializer.Serialize(simplified, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static string? TruncateString(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "...";
    }
}
