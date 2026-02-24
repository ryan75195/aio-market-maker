using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

public class PricingTask : ITask
{
    private readonly ISemanticSearchService _searchService;
    private readonly EtlDbContext _dbContext;

    public string Name => "pricing";
    public string Description => "Analyze pricing for similar listings. Usage: pricing <query> [--sold-only] [--topK=50]";

    public PricingTask(
        ISemanticSearchService searchService,
        EtlDbContext dbContext)
    {
        _searchService = searchService;
        _dbContext = dbContext;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        var query = args[0];
        var soldOnly = args.Any(a => a.Equals("--sold-only", StringComparison.OrdinalIgnoreCase));
        var topK = 50;

        foreach (var arg in args.Skip(1))
        {
            if (arg.StartsWith("--topK=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg[7..], out var k))
                {
                    topK = k;
                }
            }
        }

        System.Console.WriteLine($"Analyzing pricing for: \"{query}\"");
        System.Console.WriteLine($"Options: topK={topK}, soldOnly={soldOnly}");
        System.Console.WriteLine();

        var result = await _searchService.Search(query, topK: topK, ct: ct);

        if (result.Hits.Count == 0)
        {
            System.Console.WriteLine("No similar listings found.");
            return 0;
        }

        var listingIds = result.Hits.Select(h => h.ListingId).ToList();
        var listings = await _dbContext.Listings
            .Where(l => listingIds.Contains(l.ListingId))
            .ToListAsync(ct);

        System.Console.WriteLine($"Found {result.Hits.Count} similar listings ({listings.Count} with details)");
        System.Console.WriteLine();

        var listingDict = listings.ToDictionary(l => l.ListingId);
        var hitsByListingId = result.Hits.ToDictionary(h => h.ListingId);

        var pricedItems = listings
            .Where(l => l.Price.HasValue && l.Price > 0)
            .Where(l => !soldOnly || string.Equals(l.ListingStatus, "Sold", StringComparison.OrdinalIgnoreCase))
            .Select(l => new PricedComparable(
                l.Price!.Value,
                hitsByListingId.TryGetValue(l.ListingId, out var hit) ? hit.Score : 0.5,
                l.EndDateUtc))
            .ToList();

        if (pricedItems.Count == 0)
        {
            System.Console.WriteLine("No priced listings found for analysis.");
            return 0;
        }

        var options = new PricingOptions();
        var analysis = PricingCalculator.Analyze(pricedItems, options);

        if (analysis.SampleSize == 0)
        {
            System.Console.WriteLine("No listings remaining after outlier removal.");
            return 0;
        }

        PrintAnalysis(analysis, listings, result.Hits, listingDict);

        return 0;
    }

    private static void PrintUsage()
    {
        System.Console.WriteLine("Usage: pricing <query> [options]");
        System.Console.WriteLine();
        System.Console.WriteLine("Options:");
        System.Console.WriteLine("  --sold-only    Only include sold listings (recommended for market price)");
        System.Console.WriteLine("  --topK=N       Number of similar listings to analyze (default: 50)");
        System.Console.WriteLine();
        System.Console.WriteLine("Examples:");
        System.Console.WriteLine("  pricing \"PlayStation 5 Disc Edition Console\"");
        System.Console.WriteLine("  pricing \"PS5 Disc Edition with Controller\" --sold-only");
        System.Console.WriteLine("  pricing \"PS5 Slim 1TB\" --sold-only --topK=100");
    }

    private static void PrintAnalysis(
        PricingResult analysis,
        List<Listing> listings,
        IReadOnlyList<SemanticSearchHit> hits,
        Dictionary<string, Listing> listingDict)
    {
        System.Console.WriteLine("=" + new string('=', 79));
        System.Console.WriteLine("PRICING ANALYSIS RESULTS");
        System.Console.WriteLine("=" + new string('=', 79));
        System.Console.WriteLine();

        System.Console.WriteLine("STATISTICS:");
        System.Console.WriteLine("-" + new string('-', 79));
        System.Console.WriteLine($"  Sample Size:      {analysis.SampleSize} listings");
        System.Console.WriteLine($"  Outliers Removed: {analysis.OutliersRemoved}");
        System.Console.WriteLine($"  Confidence:       {analysis.Confidence:P0}");
        System.Console.WriteLine();

        System.Console.WriteLine("PRICING:");
        System.Console.WriteLine("-" + new string('-', 79));
        System.Console.WriteLine($"  Mean:             {analysis.Mean:C}");
        System.Console.WriteLine($"  Median:           {analysis.Median:C}");
        System.Console.WriteLine($"  Weighted Mean:    {analysis.WeightedMean:C}  (by classifier confidence)");
        if (analysis.RecencyWeightedMean.HasValue)
        {
            System.Console.WriteLine($"  Recency Weighted: {analysis.RecencyWeightedMean:C}  (by confidence + recency)");
        }
        System.Console.WriteLine();
        System.Console.WriteLine($"  Min:              {analysis.Min:C}");
        System.Console.WriteLine($"  Max:              {analysis.Max:C}");
        System.Console.WriteLine($"  Std Dev:          {analysis.StdDev:C}");
        System.Console.WriteLine();

        System.Console.WriteLine("RECOMMENDED PRICE:");
        System.Console.WriteLine("-" + new string('-', 79));
        var recommendedPrice = analysis.RecencyWeightedMean ?? analysis.WeightedMean;
        System.Console.WriteLine($"  {recommendedPrice:C}");
        System.Console.WriteLine();
        System.Console.WriteLine($"  This is the {(analysis.RecencyWeightedMean.HasValue ? "recency-weighted" : "confidence-weighted")} average,");
        System.Console.WriteLine($"  which emphasizes listings most confidently matched{(analysis.RecencyWeightedMean.HasValue ? " and recent sales" : "")}.");
        System.Console.WriteLine();

        System.Console.WriteLine("TOP MATCHES:");
        System.Console.WriteLine("-" + new string('-', 79));

        foreach (var hit in hits.Take(10))
        {
            var listing = listingDict.GetValueOrDefault(hit.ListingId);
            if (listing == null)
            {
                continue;
            }

            var title = listing.Title ?? "(no title)";
            if (title.Length > 45)
            {
                title = title[..42] + "...";
            }
            var status = listing.ListingStatus ?? "?";

            System.Console.WriteLine($"  {listing.Price,10:C}  {status,-6}  (score: {hit.Score:F3})  {title}");
        }

        System.Console.WriteLine();
    }
}
