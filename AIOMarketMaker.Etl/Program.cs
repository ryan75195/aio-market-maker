using AIOMarketMaker.Etl;
using AIOMarketMaker.Etl.Configuration;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Data.Models;
using AIOMarketMaker.Etl.Scripts;
using AIOMarketMaker.Models.Ebay;
using AIOMarketMaker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;

/*
 Goal for this app is to run on a schedule and scrape a list of search terms on ebay from a database table. It will then filter for new items and then scrape the listings
 and load them into a clean product database. It should also scrape for updates on existing listings to see if they have sold yet and update accordingly.
 It will read the search terms from an azure sql database table and write the product items to a database too. Any images will be stored in blob storage in a folder corresponding
 to an item id in the database. The config will also be a databse table, which specifies the scrape frequency
 */
public class Program
{
    public static async Task Main(string[] args)
    {
        var host = HostHelper.CreateHost(args);
        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<EtlDbContext>();

        // Handle --inspect flag
        if (args.Contains("--inspect"))
        {
            await InspectDatabase(dbContext);
            await host.StopAsync();
            return;
        }

        // Handle --reset flag (delete all listings and products)
        if (args.Contains("--reset"))
        {
            Console.WriteLine("[ETL] Resetting database - deleting all listings and products...");

            var productCount = await dbContext.Products.CountAsync();
            var listingCount = await dbContext.Listings.CountAsync();
            var historyCount = await dbContext.ListingStatusHistory.CountAsync();

            dbContext.Products.RemoveRange(dbContext.Products);
            dbContext.ListingStatusHistory.RemoveRange(dbContext.ListingStatusHistory);
            dbContext.Listings.RemoveRange(dbContext.Listings);
            await dbContext.SaveChangesAsync();

            Console.WriteLine($"[ETL] Deleted {productCount} products, {listingCount} listings, {historyCount} history records");
            await host.StopAsync();
            return;
        }

        // Handle --clear-products flag (delete only products, keep listings)
        if (args.Contains("--clear-products"))
        {
            Console.WriteLine("[ETL] Clearing products table...");

            var productCount = await dbContext.Products.CountAsync();
            dbContext.Products.RemoveRange(dbContext.Products);
            await dbContext.SaveChangesAsync();

            Console.WriteLine($"[ETL] Deleted {productCount} products");
            await host.StopAsync();
            return;
        }

        // Handle --process-existing flag (run entity resolution on existing listings without products)
        if (args.Contains("--process-existing"))
        {
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
            var dbPath = configuration["DatabasePath"] ?? "etl.db";
            var connectionString = $"Data Source={dbPath}";

            var openAiSettings = configuration.GetSection("OpenAi").Get<OpenAiSettings>()
                ?? throw new InvalidOperationException("OpenAi configuration section is required");

            await ProcessExistingListings.RunAsync(connectionString, openAiSettings);
            await host.StopAsync();
            return;
        }

        var ebayScraper = host.Services.GetRequiredService<IEbayScraper>();

        // Get all enabled scrape jobs
        var jobs = await dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .ToListAsync();

        Console.WriteLine($"[ETL] Found {jobs.Count} enabled ScrapeJobs");

        foreach (var job in jobs)
        {
            Console.WriteLine($"[ETL] Processing job {job.Id}: '{job.SearchTerm}' ({job.SearchType})");

            try
            {
                // Parse enums from job configuration
                var buyingFormat = Enum.Parse<BuyingFormat>(job.BuyingFormat, ignoreCase: true);
                var condition = Enum.Parse<Condition>(job.Condition, ignoreCase: true);

                IEnumerable<IEbayProductSummary> searchResults;

                if (job.SearchType.Equals("SOLD", StringComparison.OrdinalIgnoreCase))
                {
                    var lookbackDays = job.LookbackDays ?? 7;
                    var endDate = DateTime.UtcNow;
                    var startDate = endDate.AddDays(-lookbackDays);

                    searchResults = await ebayScraper.SearchSoldListings(
                        job.SearchTerm,
                        buyingFormat,
                        condition,
                        startDate,
                        endDate);
                }
                else
                {
                    searchResults = await ebayScraper.SearchActiveListings(
                        job.SearchTerm,
                        buyingFormat,
                        condition,
                        itemLimit: job.ItemLimit ?? 100);
                }

                var summaries = searchResults.ToList();
                Console.WriteLine($"[ETL] Found {summaries.Count} listings from search");

                // Filter out listings we already have in the database
                var existingListingIds = (await dbContext.Listings
                    .Where(l => l.ScrapeJobId == job.Id)
                    .Select(l => l.ListingId)
                    .ToListAsync())
                    .ToHashSet();

                var newListingIds = summaries
                    .Where(s => s.ListingId != null && !existingListingIds.Contains(s.ListingId))
                    .Select(s => s.ListingId!)
                    .ToArray();

                Console.WriteLine($"[ETL] {newListingIds.Length} new listings to fetch (skipping {summaries.Count - newListingIds.Length} existing)");

                if (newListingIds.Length == 0)
                {
                    Console.WriteLine($"[ETL] No new listings for job {job.Id}, skipping");
                    continue;
                }

                // Fetch full listing details
                var items = await ebayScraper.GetItemsFromListings(newListingIds);
                var products = items.ToList();

                Console.WriteLine($"[ETL] Fetched {products.Count} full listings");

                // Convert to database entities and save
                var newListings = products
                    .Where(p => p.ListingId != null)
                    .Select(p => MapToListing(p, job.Id))
                    .ToList();

                dbContext.Listings.AddRange(newListings);
                await dbContext.SaveChangesAsync();

                Console.WriteLine($"[ETL] Saved {newListings.Count} listings to database");

                // Update job's last run time
                job.LastRunUtc = DateTime.UtcNow;
                await dbContext.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ETL] Error processing job {job.Id}: {ex.Message}");
            }
        }

        // Summary
        var totalListings = await dbContext.Listings.CountAsync();
        Console.WriteLine($"[ETL] Complete. Total listings in database: {totalListings}");

        await host.StopAsync();
    }

    private static Listing MapToListing(EbayProduct ebayProduct, int scrapeJobId)
    {
        return new Listing
        {
            ListingId = ebayProduct.ListingId!,
            ScrapeJobId = scrapeJobId,
            Title = ebayProduct.Title,
            Price = ebayProduct.Price,
            Currency = ebayProduct.Currency,
            ShippingCost = ebayProduct.ShippingCost,
            Url = ebayProduct.Url,
            Condition = ebayProduct.Condition?.ToString(),
            ListingStatus = ebayProduct.ListingStatus?.ToString(),
            PurchaseFormat = ebayProduct.PurchaseFormat?.ToString(),
            Description = ebayProduct.Description,
            ItemSpecifics = ebayProduct.ItemSpecifics,
            Images = ebayProduct.Images != null ? JsonConvert.SerializeObject(ebayProduct.Images) : null,
            Location = ebayProduct.Location,
            EndDateUtc = ebayProduct.EndDateUtc,
            CreatedUtc = DateTime.UtcNow
        };
    }

    private static async Task InspectDatabase(EtlDbContext dbContext)
    {
        Console.WriteLine("=== Database Inspection ===\n");

        // ScrapeJobs
        var jobs = await dbContext.ScrapeJobs.ToListAsync();
        Console.WriteLine($"ScrapeJobs ({jobs.Count}):");
        foreach (var job in jobs)
        {
            var lastRun = job.LastRunUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never";
            Console.WriteLine($"  [{job.Id}] {job.SearchTerm} | {job.SearchType} | {job.BuyingFormat} | {job.Condition} | Enabled={job.IsEnabled} | LastRun={lastRun}");
        }

        Console.WriteLine();

        // Listings (raw scraped data)
        var listingCount = await dbContext.Listings.CountAsync();
        Console.WriteLine($"Listings ({listingCount}):");

        if (listingCount > 0)
        {
            var recentListings = await dbContext.Listings
                .OrderByDescending(l => l.Id)
                .Take(10)
                .ToListAsync();

            Console.WriteLine("  Recent listings:");
            foreach (var l in recentListings)
            {
                var title = l.Title ?? "(no title)";
                if (title.Length > 50) title = title.Substring(0, 47) + "...";
                Console.WriteLine($"    [{l.Id}] {l.ListingId} | {l.Price:F2} {l.Currency} | {l.ListingStatus} | {title}");
            }

            // Stats by status
            Console.WriteLine("\n  By Status:");
            var statusGroups = await dbContext.Listings
                .GroupBy(l => l.ListingStatus)
                .Select(g => new { Status = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var g in statusGroups)
            {
                Console.WriteLine($"    {g.Status ?? "null"}: {g.Count}");
            }

            // Price stats
            var priceStats = await dbContext.Listings
                .Where(l => l.Price.HasValue)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Min = g.Min(l => l.Price),
                    Max = g.Max(l => l.Price),
                    Avg = g.Average(l => l.Price)
                })
                .FirstOrDefaultAsync();

            if (priceStats != null)
            {
                Console.WriteLine($"\n  Price Stats: Min={priceStats.Min:F2}, Max={priceStats.Max:F2}, Avg={priceStats.Avg:F2}");
            }
        }

        Console.WriteLine();

        // Products (normalized/classified data)
        var productCount = await dbContext.Products.CountAsync();
        Console.WriteLine($"Products (normalized) ({productCount}):");

        if (productCount > 0)
        {
            // Stats by category
            Console.WriteLine("  By Category:");
            var categoryGroups = await dbContext.Products
                .GroupBy(p => p.Category)
                .Select(g => new { Category = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var g in categoryGroups)
            {
                Console.WriteLine($"    {g.Category}: {g.Count}");
            }

            // Stats by brand
            Console.WriteLine("\n  By Brand (top 10):");
            var brandGroups = await dbContext.Products
                .Where(p => p.Brand != null)
                .GroupBy(p => p.Brand)
                .Select(g => new { Brand = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .Take(10)
                .ToListAsync();
            foreach (var g in brandGroups)
            {
                Console.WriteLine($"    {g.Brand}: {g.Count}");
            }
        }
    }
}
