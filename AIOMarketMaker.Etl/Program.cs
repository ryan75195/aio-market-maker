using AIOMarketMaker.Etl;
using AIOMarketMaker.Etl.Data;
using AIOMarketMaker.Etl.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

        // Handle --reset flag (delete all listings)
        if (args.Contains("--reset"))
        {
            Console.WriteLine("[ETL] Resetting database - deleting all listings...");

            var listingCount = await dbContext.Listings.CountAsync();
            var historyCount = await dbContext.ListingStatusHistory.CountAsync();

            dbContext.ListingStatusHistory.RemoveRange(dbContext.ListingStatusHistory);
            dbContext.Listings.RemoveRange(dbContext.Listings);
            await dbContext.SaveChangesAsync();

            Console.WriteLine($"[ETL] Deleted {listingCount} listings, {historyCount} history records");
            await host.StopAsync();
            return;
        }

        var jobRunner = host.Services.GetRequiredService<IJobRunner>();

        // Get all enabled scrape jobs
        var jobs = await dbContext.ScrapeJobs
            .Where(j => j.IsEnabled)
            .ToListAsync();

        Console.WriteLine($"[ETL] Found {jobs.Count} enabled ScrapeJobs");

        foreach (var job in jobs)
        {
            Console.WriteLine($"[ETL] Processing job {job.Id}: '{job.SearchTerm}'");

            try
            {
                var result = await jobRunner.RunJob(job);

                if (result.Success)
                {
                    Console.WriteLine($"[ETL] Job {job.Id} complete: {result.ListingsFound} found, {result.NewListingsFetched} new");
                }
                else
                {
                    Console.WriteLine($"[ETL] Job {job.Id} failed: {result.Error}");
                }
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

    private static async Task InspectDatabase(EtlDbContext dbContext)
    {
        Console.WriteLine("=== Database Inspection ===\n");

        // ScrapeJobs
        var jobs = await dbContext.ScrapeJobs.ToListAsync();
        Console.WriteLine($"ScrapeJobs ({jobs.Count}):");
        foreach (var job in jobs)
        {
            var lastRun = job.LastRunUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never";
            var filter = !string.IsNullOrEmpty(job.FilterInstructions) ? $" | Filter: {job.FilterInstructions}" : "";
            Console.WriteLine($"  [{job.Id}] {job.SearchTerm}{filter} | Enabled={job.IsEnabled} | LastRun={lastRun}");
        }

        Console.WriteLine();

        // Listings
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

            // Stats by condition
            Console.WriteLine("\n  By Condition:");
            var conditionGroups = await dbContext.Listings
                .GroupBy(l => l.Condition)
                .Select(g => new { Condition = g.Key, Count = g.Count() })
                .ToListAsync();
            foreach (var g in conditionGroups)
            {
                Console.WriteLine($"    {g.Condition ?? "null"}: {g.Count}");
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

        // Status history
        var historyCount = await dbContext.ListingStatusHistory.CountAsync();
        Console.WriteLine($"\nListingStatusHistory ({historyCount})");
    }
}
