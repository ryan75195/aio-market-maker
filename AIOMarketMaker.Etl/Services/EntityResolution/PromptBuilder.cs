using System.Text;
using System.Text.Json;
using AIOMarketMaker.Etl.Services.VectorSearch;
using AIOMarketMaker.Models.Ebay;

namespace AIOMarketMaker.Etl.Services.EntityResolution;

/// <summary>
/// Builds prompts for entity resolution using an LLM.
/// </summary>
public class PromptBuilder
{
    private const int MaxDescriptionLength = 500;

    private const string AttributeGuidelines = """
        ## Attribute Extraction Guidelines

        ### productName (CRITICAL for consistency)
        - Use the BASE PRODUCT LINE name only, NOT including variants or editions
        - Examples:
          - "PlayStation 5" (NOT "PlayStation 5 Pro" or "PS5 Digital Edition")
          - "iPhone 15" (NOT "iPhone 15 Pro Max")
          - "Nintendo Switch" (NOT "Nintendo Switch OLED")
          - "DualSense Controller" (NOT "DualSense Edge")
        - Put variant info (Pro, Digital, OLED, Edge, etc.) in the 'edition' field instead
        - Use official marketing capitalization and spacing
        - Be consistent: all variants of the same product line share the SAME productName
        - For accessories, name the accessory itself, not what it's compatible with

        ### edition (CAPTURES ALL VARIANTS)
        - Captures the specific product variant, revision, model tier, or special edition
        - Examples: "Pro", "Digital Edition", "OLED Model", "Slim", "Edge", "Max", "Plus"
        - Use the official edition name from the manufacturer
        - If multiple editions exist (e.g., storage + color variant), prefer the functional variant

        ### storageCapacity
        - Normalize to compact format: use GB or TB without spaces (e.g., '256GB', '1TB')
        - Only populate for products where storage is a key differentiator

        ### color
        - Use the official marketing color name from the manufacturer
        - For special editions with unique colorways, include the edition context

        ### model
        - The specific model number or SKU if visible in title or itemSpecifics
        - Look in itemSpecifics fields: 'Model', 'MPN', 'Part Number'
        - Useful for distinguishing hardware revisions within the same product line

        ### variantType
        - Use for variants not captured by other fields
        - Examples: regional variants, carrier-specific versions, bundle configurations

        IMPORTANT:
        - Extract from BOTH title AND itemSpecifics - do not leave null if info exists
        - Consistency is critical: identical products must have identical attribute values
        """;

    public string SystemPrompt => """
        You are a product classification expert. Your task is to analyze product listings and:
        1. Classify each product into an absolute category (what the item IS, not whether it matches a search)
        2. Extract and normalize key product attributes

        You must respond with valid JSON following the exact schema provided.
        Be precise and consistent in your classifications.
        """;

    public string SingleProductSystemPrompt => """
        You are a product classification expert. Analyze the product listing and:
        1. Classify it into an absolute category (what the item IS)
        2. Extract and normalize key product attributes

        Respond with a single JSON object (not an array).
        Be precise and consistent.
        """;

    public string BuildUserPrompt(IReadOnlyList<EbayProduct> products)
        => BuildUserPrompt(products, null);

    public string BuildUserPrompt(
        IReadOnlyList<EbayProduct> products,
        IReadOnlyDictionary<string, IReadOnlyList<SimilarProductName>>? similarNames)
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
        sb.AppendLine(AttributeGuidelines);
        sb.AppendLine();
        sb.AppendLine("## Products to Classify");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(SerializeProducts(products));
        sb.AppendLine("```");
        sb.AppendLine();

        // Add similar product names section if available
        if (similarNames != null && similarNames.Count > 0)
        {
            sb.AppendLine("## Existing Product Names (Use for Consistency)");
            sb.AppendLine();
            sb.AppendLine("For the following listings, similar products already exist in our database.");
            sb.AppendLine("**STRONGLY prefer using an existing ProductName if the listing is the same product.**");
            sb.AppendLine();

            foreach (var product in products.Where(p => p.ListingId != null && similarNames.ContainsKey(p.ListingId)))
            {
                var matches = similarNames[product.ListingId!];
                sb.AppendLine($"Listing \"{product.ListingId}\" (title: \"{TruncateString(product.Title, 60)}\"):");
                foreach (var match in matches.Take(3))
                {
                    var info = new List<string>();
                    if (!string.IsNullOrEmpty(match.Category)) info.Add($"category: {match.Category}");
                    if (!string.IsNullOrEmpty(match.Brand)) info.Add($"brand: {match.Brand}");
                    info.Add($"score: {match.Score:F2}");
                    sb.AppendLine($"  - \"{match.ProductName}\" ({string.Join(", ", info)})");
                }
                sb.AppendLine();
            }
        }

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
                    "model": "CFI-2000",
                    "storageCapacity": "1TB",
                    "color": "White",
                    "edition": "Pro",
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
        sb.AppendLine("IMPORTANT: productName should be the BASE product line (e.g., 'PlayStation 5' for all PS5 variants). Put 'Pro', 'Digital Edition', etc. in the edition field.");

        return sb.ToString();
    }

    public string BuildSingleProductPrompt(
        EbayProduct product,
        IReadOnlyList<SimilarProductName>? similarNames)
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
        sb.AppendLine(AttributeGuidelines);
        sb.AppendLine();
        sb.AppendLine("## Product to Classify");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(SerializeSingleProduct(product));
        sb.AppendLine("```");
        sb.AppendLine();

        // Add similar product names if available
        if (similarNames != null && similarNames.Count > 0)
        {
            sb.AppendLine("## Existing Product Names (Use for Consistency)");
            sb.AppendLine();
            sb.AppendLine("Similar products already exist in our database.");
            sb.AppendLine("**STRONGLY prefer using an existing ProductName if this is the same product.**");
            sb.AppendLine();

            foreach (var match in similarNames.Take(3))
            {
                var info = new List<string>();
                if (!string.IsNullOrEmpty(match.Category)) info.Add($"category: {match.Category}");
                if (!string.IsNullOrEmpty(match.Brand)) info.Add($"brand: {match.Brand}");
                info.Add($"score: {match.Score:F2}");
                sb.AppendLine($"  - \"{match.ProductName}\" ({string.Join(", ", info)})");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Required Response Format");
        sb.AppendLine();
        sb.AppendLine("Respond with a single JSON object (NOT an array):");
        sb.AppendLine("```json");
        sb.AppendLine("""
            {
              "category": "base_product",
              "confidence": 0.95,
              "productName": "PlayStation 5",
              "attributes": {
                "brand": "Sony",
                "model": "CFI-2000",
                "storageCapacity": "1TB",
                "color": "White",
                "edition": "Pro",
                "variantType": null
              },
              "bundledItems": null
            }
            """);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("For bundles, list bundledItems as an array of item descriptions.");
        sb.AppendLine("Set attributes to null if not determinable from the listing.");
        sb.AppendLine("IMPORTANT: productName should be the BASE product line (e.g., 'PlayStation 5' for all PS5 variants). Put 'Pro', 'Digital Edition', etc. in the edition field.");

        return sb.ToString();
    }

    private string SerializeSingleProduct(EbayProduct product)
    {
        var simplified = new
        {
            listingId = product.ListingId,
            title = product.Title,
            price = product.Price,
            condition = product.Condition?.ToString(),
            itemSpecifics = TruncateString(product.ItemSpecifics, 1000),
            description = TruncateString(product.Description, MaxDescriptionLength)
        };

        return JsonSerializer.Serialize(simplified, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
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
