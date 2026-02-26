using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Console.Tasks;

public class KAnalysisTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IVectorIndex _vectorIndex;
    private readonly IVariantClassifierClient _classifier;

    public string Name => "k-analysis";
    public string Description => "Analyze optimal K value for vector search by sampling active listings";

    public KAnalysisTask(
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
        const int maxK = 500;
        const int sampleSize = 20;

        // Parse optional --sample N
        var sampleArg = Array.IndexOf(args, "--sample");
        var actualSample = sampleArg >= 0 && sampleArg + 1 < args.Length && int.TryParse(args[sampleArg + 1], out var s) ? s : sampleSize;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        System.Console.WriteLine($"K-Analysis: sampling {actualSample} active listings, querying vector index with K={maxK}");
        System.Console.WriteLine();

        // Load all listings into lookup dictionary
        System.Console.Write("Loading listings...");
        var allListings = await db.Listings
            .AsNoTracking()
            .ToDictionaryAsync(l => l.ListingId, ct);
        var listingsById = allListings.Values.ToDictionary(l => l.Id);
        System.Console.WriteLine($" {allListings.Count:N0} listings loaded.");

        // Sample active listings spread across different scrape jobs (from in-memory data)
        var activeListings = allListings.Values
            .Where(l => l.ListingStatus == "Active")
            .GroupBy(l => l.ScrapeJobId)
            .SelectMany(g => g.OrderBy(l => l.Id).Take(3))
            .Take(actualSample)
            .ToList();

        System.Console.WriteLine($"Sampled {activeListings.Count} active listings from {activeListings.Select(l => l.ScrapeJobId).Distinct().Count()} jobs:");
        foreach (var l in activeListings)
        {
            System.Console.WriteLine($"  [{l.Id}] {l.Title?[..Math.Min(l.Title.Length, 70)]}");
        }
        System.Console.WriteLine();

        // Track results: rank -> (accepted, rejected, similarities)
        var rankResults = new Dictionary<int, (int Accepted, int Rejected, List<float> Scores, List<float> Confidences)>();
        for (var r = 1; r <= maxK; r++)
        {
            rankResults[r] = (0, 0, new List<float>(), new List<float>());
        }

        var totalQueries = 0;
        var totalPairs = 0;

        foreach (var listing in activeListings)
        {
            totalQueries++;
            System.Console.Write($"[{totalQueries}/{activeListings.Count}] Querying {listing.ListingId}...");

            // Query vector index directly -- bypass SemanticSearchService threshold filter
            var neighbors = _vectorIndex.SearchById(listing.ListingId, maxK + 1)
                .Where(h => h.Id != listing.ListingId)
                .Take(maxK)
                .ToList();

            System.Console.Write($" {neighbors.Count} neighbors.");

            if (neighbors.Count == 0)
            {
                System.Console.WriteLine(" (no results)");
                continue;
            }

            // Look up neighbor listings and build classify requests per rank
            var pairsToClassify = new List<(int Rank, float Score, ClassifyPairRequest Request)>();

            for (var i = 0; i < neighbors.Count; i++)
            {
                var neighbor = neighbors[i];
                if (!allListings.TryGetValue(neighbor.Id, out var neighborListing))
                {
                    continue; // Listing not in database (deleted?)
                }

                pairsToClassify.Add((
                    Rank: i + 1,
                    Score: neighbor.Score,
                    Request: new ClassifyPairRequest(
                        listing.Title ?? "", listing.Description ?? "",
                        neighborListing.Title ?? "", neighborListing.Description ?? "")
                ));
            }

            // Classify in batches of 128
            var allRequests = pairsToClassify.Select(p => p.Request).ToList();
            var results = await _classifier.Classify(allRequests);

            var accepted = 0;
            for (var i = 0; i < pairsToClassify.Count; i++)
            {
                var (rank, score, _) = pairsToClassify[i];
                var result = results[i];

                var entry = rankResults[rank];
                if (result.IsComparable)
                {
                    entry.Accepted++;
                    accepted++;
                }
                else
                {
                    entry.Rejected++;
                }
                entry.Scores.Add(score);
                entry.Confidences.Add(result.Confidence);
                rankResults[rank] = entry;
            }

            totalPairs += pairsToClassify.Count;
            System.Console.WriteLine($" Classified {pairsToClassify.Count} pairs, {accepted} accepted.");
        }

        // Print results table
        System.Console.WriteLine();
        System.Console.WriteLine($"{'=',-60}");
        System.Console.WriteLine($"K-ANALYSIS RESULTS ({actualSample} listings, K={maxK})");
        System.Console.WriteLine($"{'=',-60}");
        System.Console.WriteLine();
        System.Console.WriteLine($"{"Rank",-8} {"Accepted",10} {"Rejected",10} {"Accept%",10} {"AvgSim",10} {"AvgConf",10} {"Cumul.Acc",10}");
        System.Console.WriteLine($"{new string('-', 8)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}");

        var cumulativeAccepted = 0;
        var lastRankWithAcceptance = 0;

        for (var rank = 1; rank <= maxK; rank++)
        {
            var entry = rankResults[rank];
            var total = entry.Accepted + entry.Rejected;
            if (total == 0)
            {
                break;
            }

            cumulativeAccepted += entry.Accepted;
            var acceptPct = 100.0 * entry.Accepted / total;
            var avgSim = entry.Scores.Count > 0 ? entry.Scores.Average() : 0f;
            var avgConf = entry.Confidences.Count > 0 ? entry.Confidences.Average() : 0f;

            if (entry.Accepted > 0)
            {
                lastRankWithAcceptance = rank;
            }

            // Print every rank up to 30, then every 10th, then every 50th
            var shouldPrint = rank <= 30 || rank % 10 == 0 || rank % 50 == 0 || rank == lastRankWithAcceptance;
            if (shouldPrint || entry.Accepted > 0)
            {
                System.Console.WriteLine($"{rank,-8} {entry.Accepted,10} {entry.Rejected,10} {acceptPct,9:F1}% {avgSim,10:F4} {avgConf,10:F4} {cumulativeAccepted,10}");
            }
        }

        // Summary
        System.Console.WriteLine();
        System.Console.WriteLine($"Total pairs classified: {totalPairs:N0}");
        System.Console.WriteLine($"Total accepted:         {cumulativeAccepted:N0}");
        System.Console.WriteLine($"Last rank with accept:  {lastRankWithAcceptance}");
        System.Console.WriteLine();

        // Bucket summary
        System.Console.WriteLine("Bucket Summary:");
        System.Console.WriteLine($"{"Bucket",-15} {"Accepted",10} {"Rejected",10} {"Accept%",10}");
        System.Console.WriteLine($"{new string('-', 15)} {new string('-', 10)} {new string('-', 10)} {new string('-', 10)}");

        var buckets = new[] { (1, 10), (11, 20), (21, 30), (31, 50), (51, 100), (101, 200), (201, 300), (301, 500) };
        foreach (var (lo, hi) in buckets)
        {
            var bucketAcc = 0;
            var bucketRej = 0;
            for (var r = lo; r <= hi; r++)
            {
                bucketAcc += rankResults[r].Accepted;
                bucketRej += rankResults[r].Rejected;
            }
            var bucketTotal = bucketAcc + bucketRej;
            if (bucketTotal == 0)
            {
                break;
            }
            var pct = 100.0 * bucketAcc / bucketTotal;
            System.Console.WriteLine($"{"K=" + lo + "-" + hi,-15} {bucketAcc,10} {bucketRej,10} {pct,9:F1}%");
        }

        return 0;
    }
}
