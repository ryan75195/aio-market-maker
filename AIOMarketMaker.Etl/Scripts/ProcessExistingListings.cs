// Run with: dotnet run --project AIOMarketMaker.Etl -- --process-existing
// Or execute directly in the ETL project

using AIOMarketMaker.Core.Configuration;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Core.Services.EntityResolution;
using AIOMarketMaker.Core.Services.VectorSearch;
using AIOMarketMaker.Models.Ebay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenAI;

namespace AIOMarketMaker.Etl.Scripts;

public static class ProcessExistingListings
{
    public static async Task RunAsync(
        string connectionString,
        OpenAiSettings openAiSettings,
        PineconeSettings? pineconeSettings,
        EmbeddingSettings embeddingSettings)
    {
        // Set up logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<OpenAiEntityResolutionService>();
        var indexerLogger = loggerFactory.CreateLogger<ProductNameIndexer>();

        // Set up database
        var options = new DbContextOptionsBuilder<EtlDbContext>()
            .UseSqlite(connectionString)
            .Options;

        using var dbContext = new EtlDbContext(options);

        // Set up OpenAI service
        var client = new OpenAIClient(openAiSettings.ApiKey);
        var promptBuilder = new PromptBuilder();

        // Set up Pinecone indexer (real or no-op based on config)
        IProductNameIndexer productNameIndexer;
        if (pineconeSettings != null && !string.IsNullOrEmpty(pineconeSettings.ApiKey))
        {
            var embeddingService = new OpenAiEmbeddingService(client, embeddingSettings, loggerFactory.CreateLogger<OpenAiEmbeddingService>());
            var pineconeService = new PineconeService(pineconeSettings, loggerFactory.CreateLogger<PineconeService>());
            productNameIndexer = new ProductNameIndexer(embeddingService, pineconeService, pineconeSettings, indexerLogger);
            Console.WriteLine("Pinecone indexing enabled");
        }
        else
        {
            productNameIndexer = new NoOpProductNameIndexer();
            Console.WriteLine("Pinecone indexing disabled (no config)");
        }

        var entityResolutionService = new OpenAiEntityResolutionService(client, openAiSettings, promptBuilder, productNameIndexer, logger);

        Console.WriteLine("=== Processing Existing Listings ===");
        Console.WriteLine();

        // Find listings without products
        var existingProductListingIds = await dbContext.Products
            .Select(p => p.ListingId)
            .ToListAsync();

        var listingsWithoutProducts = await dbContext.Listings
            .Where(l => !existingProductListingIds.Contains(l.Id))
            .ToListAsync();

        Console.WriteLine($"Found {listingsWithoutProducts.Count} listings without products");

        if (listingsWithoutProducts.Count == 0)
        {
            Console.WriteLine("Nothing to process!");
            return;
        }

        // Convert listings to EbayProduct format for entity resolution
        var ebayProducts = listingsWithoutProducts
            .Select(l => ConvertToEbayProduct(l))
            .ToList();

        // Process in batches
        var batchSize = openAiSettings.BatchSize;
        var totalBatches = (int)Math.Ceiling(ebayProducts.Count / (double)batchSize);
        var processedCount = 0;
        var errorCount = 0;

        for (int i = 0; i < ebayProducts.Count; i += batchSize)
        {
            var batchNum = (i / batchSize) + 1;
            var batch = ebayProducts.Skip(i).Take(batchSize).ToList();

            Console.WriteLine($"\nProcessing batch {batchNum}/{totalBatches} ({batch.Count} items)...");

            try
            {
                var results = await entityResolutionService.Resolve(batch);

                // Build lookup from eBay ListingId to database Listing
                var listingLookup = listingsWithoutProducts
                    .Where(l => batch.Any(b => b.ListingId == l.ListingId))
                    .ToDictionary(l => l.ListingId, l => l);

                // Map to Product entities
                var products = results
                    .Where(r => listingLookup.ContainsKey(r.ListingId))
                    .Select(r => MapToProduct(r, listingLookup[r.ListingId]))
                    .ToList();

                dbContext.Products.AddRange(products);
                await dbContext.SaveChangesAsync();

                processedCount += products.Count;
                Console.WriteLine($"  Saved {products.Count} products");

                // Index product names in Pinecone
                try
                {
                    await productNameIndexer.IndexNewProductNames(products);
                    Console.WriteLine($"  Indexed {products.Count} product names in Pinecone");
                }
                catch (Exception indexEx)
                {
                    Console.WriteLine($"  WARNING: Failed to index in Pinecone: {indexEx.Message}");
                }

                foreach (var p in products)
                {
                    var listing = listingLookup.Values.First(l => l.Id == p.ListingId);
                    Console.WriteLine($"    [{p.Category}] {listing.Title?.Substring(0, Math.Min(50, listing.Title?.Length ?? 0))}...");
                }
            }
            catch (Exception ex)
            {
                errorCount += batch.Count;
                Console.WriteLine($"  ERROR: {ex.Message}");
            }

            // Small delay between batches to avoid rate limiting
            if (i + batchSize < ebayProducts.Count)
            {
                await Task.Delay(1000);
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Complete ===");
        Console.WriteLine($"Processed: {processedCount}");
        Console.WriteLine($"Errors: {errorCount}");
    }

    private static EbayProduct ConvertToEbayProduct(Listing listing)
    {
        Condition? condition = null;
        if (!string.IsNullOrEmpty(listing.Condition) &&
            Enum.TryParse<Condition>(listing.Condition, true, out var parsedCondition))
        {
            condition = parsedCondition;
        }

        return new EbayProduct(
            ListingId: listing.ListingId,
            Title: listing.Title,
            Price: listing.Price,
            Currency: listing.Currency,
            ShippingCost: listing.ShippingCost,
            Url: listing.Url,
            Condition: condition,
            Images: string.IsNullOrEmpty(listing.Images) ? null : JsonConvert.DeserializeObject<List<string>>(listing.Images),
            ListingStatus: Enum.TryParse<EbayListingStatus>(listing.ListingStatus, true, out var status) ? status : null,
            PurchaseFormat: Enum.TryParse<PurchaseFormat>(listing.PurchaseFormat, true, out var format) ? format : null,
            Description: listing.Description,
            ItemSpecifics: listing.ItemSpecifics,
            EndDateUtc: listing.EndDateUtc,
            Location: listing.Location
        );
    }

    private static Product MapToProduct(EntityResolutionResult resolution, Listing listing)
    {
        DateTime? listedDateUtc = null;
        DateTime? soldDateUtc = null;

        if (listing.ListingStatus == "Sold")
        {
            soldDateUtc = listing.EndDateUtc;
        }
        else
        {
            listedDateUtc = listing.CreatedUtc;
        }

        return new Product
        {
            ListingId = listing.Id,
            Category = resolution.Category,
            CategoryConfidence = resolution.CategoryConfidence,
            ProductName = resolution.ProductName,
            Brand = resolution.Attributes.Brand,
            Model = resolution.Attributes.Model,
            StorageCapacity = resolution.Attributes.StorageCapacity,
            Color = resolution.Attributes.Color,
            Edition = resolution.Attributes.Edition,
            VariantType = resolution.Attributes.VariantType,
            BundledItems = resolution.BundledItems != null
                ? JsonConvert.SerializeObject(resolution.BundledItems)
                : null,
            // Denormalized from Listing
            EbayListingId = listing.ListingId,
            Title = listing.Title,
            Price = listing.Price,
            Currency = listing.Currency,
            ShippingCost = listing.ShippingCost,
            Url = listing.Url,
            Condition = listing.Condition,
            ListingStatus = listing.ListingStatus,
            PurchaseFormat = listing.PurchaseFormat,
            Location = listing.Location,
            EndDateUtc = listing.EndDateUtc,
            ListedDateUtc = listedDateUtc,
            SoldDateUtc = soldDateUtc,
            ResolvedUtc = DateTime.UtcNow
        };
    }
}
