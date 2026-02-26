using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Console.Tasks;

public class ValidationTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IVectorIndex _vectorIndex;
    private readonly IVariantClassifierClient _classifier;

    public string Name => "validate";
    public string Description => "Validate vector search + classifier results across sample categories";

    public ValidationTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        IVectorIndex vectorIndex,
        IVariantClassifierClient classifier)
    {
        _dbFactory = dbFactory;
        _vectorIndex = vectorIndex;
        _classifier = classifier;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        const int neighborsPerListing = 20;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Load all listings
        System.Console.Write("Loading listings...");
        var allListings = await db.Listings
            .AsNoTracking()
            .ToDictionaryAsync(l => l.ListingId, ct);
        var listingsById = allListings.Values.ToDictionary(l => l.Id);
        System.Console.WriteLine($" {allListings.Count:N0} loaded.");

        // Load scrape jobs for category names
        var jobs = await db.ScrapeJobs.AsNoTracking().ToDictionaryAsync(j => j.Id, ct);

        // Pick interesting categories to validate - mix of easy and hard
        var targetJobTerms = new[] {
            "PlayStation 5 Console", "RTX 4090 Graphics Card", "Dyson Airwrap",
            "Nike Air Jordan 1", "Cartier Love Bracelet", "Rolex Submariner",
            "LEGO Star Wars Set", "Moissanite Engagement Ring", "Canada Goose",
            "Mac Mini", "Sonos One Speaker", "TaylorMade Stealth 2 Driver"
        };

        var targetJobIds = jobs.Values
            .Where(j => targetJobTerms.Any(t => j.SearchTerm.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Select(j => j.Id)
            .ToHashSet();

        // Sample 2 active listings per category
        var activeListings = allListings.Values
            .Where(l => l.ListingStatus == "Active" && targetJobIds.Contains(l.ScrapeJobId))
            .GroupBy(l => l.ScrapeJobId)
            .SelectMany(g => g.OrderByDescending(l => l.Id).Take(2))
            .ToList();

        System.Console.WriteLine($"Validating {activeListings.Count} listings across {activeListings.Select(l => l.ScrapeJobId).Distinct().Count()} categories");
        System.Console.WriteLine(new string('=', 120));

        foreach (var listing in activeListings)
        {
            var jobName = jobs.TryGetValue(listing.ScrapeJobId, out var job) ? job.SearchTerm : "Unknown";

            System.Console.WriteLine();
            System.Console.WriteLine($"[{jobName}] ACTIVE: {listing.Title}");
            System.Console.WriteLine($"  Price: {listing.Price:C} | Condition: {listing.Condition} | Id: {listing.Id}");
            System.Console.WriteLine($"  Desc: {(listing.Description ?? "")[..Math.Min((listing.Description ?? "").Length, 150)]}");
            System.Console.WriteLine();

            // Query vector index
            var neighbors = _vectorIndex.SearchById(listing.ListingId, neighborsPerListing + 1)
                .Where(h => h.Id != listing.ListingId)
                .Take(neighborsPerListing)
                .ToList();

            if (neighbors.Count == 0)
            {
                System.Console.WriteLine("  (no vector neighbors found)");
                continue;
            }

            // Build classify requests
            var pairsToClassify = new List<(float Score, Listing Neighbor, ClassifyPairRequest Request)>();
            foreach (var neighbor in neighbors)
            {
                if (!allListings.TryGetValue(neighbor.Id, out var neighborListing))
                {
                    continue;
                }
                pairsToClassify.Add((
                    Score: neighbor.Score,
                    Neighbor: neighborListing,
                    Request: new ClassifyPairRequest(
                        listing.Title ?? "", listing.Description ?? "",
                        neighborListing.Title ?? "", neighborListing.Description ?? "")));
            }

            // Classify batch
            var requests = pairsToClassify.Select(p => p.Request).ToList();
            var results = await _classifier.Classify(requests, ct);

            // Print results
            for (var i = 0; i < pairsToClassify.Count; i++)
            {
                var pair = pairsToClassify[i];
                var result = results[i];
                var verdict = result.IsComparable ? "MATCH" : "REJECT";
                var marker = result.IsComparable ? "+" : "-";
                var status = pair.Neighbor.ListingStatus ?? "?";
                var price = pair.Neighbor.Price;

                System.Console.WriteLine($"  {marker} [{verdict}] conf={result.Confidence:F3} sim={pair.Score:F3} | {status} {price:C} | {pair.Neighbor.Title?[..Math.Min(pair.Neighbor.Title?.Length ?? 0, 80)]}");
            }

            System.Console.WriteLine($"  --- {results.Count(r => r.IsComparable)}/{pairsToClassify.Count} accepted ---");
        }

        return 0;
    }
}
