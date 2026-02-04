using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

public class PricingTask : ITask
{
    private readonly ISemanticSearchService _searchService;
    private readonly IPricingAnalysisService _pricingService;
    private readonly EtlDbContext _dbContext;

    public string Name => "pricing";
    public string Description => "Analyze pricing for similar listings. Usage: pricing <query> [--sold-only] [--topK=50]";

    public PricingTask(
        ISemanticSearchService searchService,
        IPricingAnalysisService pricingService,
        EtlDbContext dbContext)
    {
        _searchService = searchService;
        _pricingService = pricingService;
        _dbContext = dbContext;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // Parse arguments
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

        // Get semantic search results
        var result = await _searchService.Search(query, topK: topK, ct: ct);

        if (result.Hits.Count == 0)
        {
            System.Console.WriteLine("No similar listings found.");
            return 0;
        }

        // Get listing details
        var listingIds = result.Hits.Select(h => h.ListingId).ToList();
        var listings = await _dbContext.Listings
            .Where(l => listingIds.Contains(l.ListingId))
            .ToListAsync(ct);

        System.Console.WriteLine($"Found {result.Hits.Count} similar listings ({listings.Count} with details)");
        System.Console.WriteLine();

        // Run pricing analysis
        var options = new PricingAnalysisOptions
        {
            SoldOnly = soldOnly,
            IqrMultiplier = 1.5,
            SimilarityWeightPower = 2.0,
            RecencyHalfLifeDays = 30.0
        };

        var analysis = _pricingService.Analyze(listings, result.Hits, options);

        if (analysis.SampleSize == 0)
        {
            System.Console.WriteLine("No priced listings found for analysis.");
            return 0;
        }

        // Print results
        PrintAnalysis(analysis, listings, result.Hits);

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
        PricingAnalysisResult analysis,
        List<Listing> listings,
        IReadOnlyList<SemanticSearchHit> hits)
    {
        var listingDict = listings.ToDictionary(l => l.ListingId);

        System.Console.WriteLine("=" + new string('=', 79));
        System.Console.WriteLine("PRICING ANALYSIS RESULTS");
        System.Console.WriteLine("=" + new string('=', 79));
        System.Console.WriteLine();

        // Summary stats
        System.Console.WriteLine("STATISTICS:");
        System.Console.WriteLine("-" + new string('-', 79));
        System.Console.WriteLine($"  Sample Size:      {analysis.SampleSize} listings");
        System.Console.WriteLine($"  Outliers Removed: {analysis.OutliersRemoved}");
        System.Console.WriteLine($"  Confidence:       {analysis.Confidence:P0}");
        System.Console.WriteLine();

        // Pricing
        System.Console.WriteLine("PRICING:");
        System.Console.WriteLine("-" + new string('-', 79));
        System.Console.WriteLine($"  Mean:             {analysis.Mean:C}");
        System.Console.WriteLine($"  Median:           {analysis.Median:C}");
        System.Console.WriteLine($"  Weighted Mean:    {analysis.WeightedMean:C}  (by similarity score)");
        if (analysis.RecencyWeightedMean.HasValue)
        {
            System.Console.WriteLine($"  Recency Weighted: {analysis.RecencyWeightedMean:C}  (by similarity + recency)");
        }
        System.Console.WriteLine();
        System.Console.WriteLine($"  Min:              {analysis.Min:C}");
        System.Console.WriteLine($"  Max:              {analysis.Max:C}");
        System.Console.WriteLine($"  Std Dev:          {analysis.StdDev:C}");
        System.Console.WriteLine();
        System.Console.WriteLine($"  Price Range:      {analysis.PriceRange.Low:C} - {analysis.PriceRange.High:C}  (median +/- 1 std dev)");
        System.Console.WriteLine();

        // Recommendation
        System.Console.WriteLine("RECOMMENDED PRICE:");
        System.Console.WriteLine("-" + new string('-', 79));
        var recommendedPrice = analysis.RecencyWeightedMean ?? analysis.WeightedMean;
        System.Console.WriteLine($"  {recommendedPrice:C}");
        System.Console.WriteLine();
        System.Console.WriteLine($"  This is the {(analysis.RecencyWeightedMean.HasValue ? "recency-weighted" : "similarity-weighted")} average,");
        System.Console.WriteLine($"  which emphasizes listings most similar to your query{(analysis.RecencyWeightedMean.HasValue ? " and recent sales" : "")}.");
        System.Console.WriteLine();

        // Outliers
        if (analysis.Outliers.Count > 0)
        {
            System.Console.WriteLine("OUTLIERS (excluded from analysis):");
            System.Console.WriteLine("-" + new string('-', 79));
            foreach (var outlier in analysis.Outliers.OrderByDescending(o => o.Price))
            {
                var listing = listingDict.GetValueOrDefault(outlier.ListingId);
                var title = listing?.Title ?? "(unknown)";
                if (title.Length > 50) title = title[..47] + "...";
                System.Console.WriteLine($"  {outlier.Price,10:C}  (score: {outlier.Score:F3})  {title}");
            }
            System.Console.WriteLine();
        }

        // Top matches
        System.Console.WriteLine("TOP MATCHES (included in analysis):");
        System.Console.WriteLine("-" + new string('-', 79));

        var includedIds = hits
            .Where(h => !analysis.Outliers.Any(o => o.ListingId == h.ListingId))
            .Take(10)
            .ToList();

        foreach (var hit in includedIds)
        {
            var listing = listingDict.GetValueOrDefault(hit.ListingId);
            if (listing == null) continue;

            var title = listing.Title ?? "(no title)";
            if (title.Length > 45) title = title[..42] + "...";
            var status = listing.ListingStatus ?? "?";

            System.Console.WriteLine($"  {listing.Price,10:C}  {status,-6}  (score: {hit.Score:F3})  {title}");
        }

        System.Console.WriteLine();
    }
}
