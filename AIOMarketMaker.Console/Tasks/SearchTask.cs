using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

public class SearchTask : ITask
{
    private readonly ISemanticSearchService _searchService;
    private readonly EtlDbContext _dbContext;

    public string Name => "search";
    public string Description => "Search listings by semantic query. Usage: search <query> [topK]";

    public SearchTask(ISemanticSearchService searchService, EtlDbContext dbContext)
    {
        _searchService = searchService;
        _dbContext = dbContext;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            System.Console.WriteLine("Usage: search <query> [topK]");
            System.Console.WriteLine("Example: search \"PlayStation 5 console\" 10");
            return 1;
        }

        var query = args[0];
        var topK = args.Length > 1 && int.TryParse(args[1], out var k) ? k : 10;

        System.Console.WriteLine($"Searching for: \"{query}\" (top {topK})");
        System.Console.WriteLine();

        var result = await _searchService.SearchAsync(query, topK: topK, ct: ct);

        if (result.Hits.Count == 0)
        {
            System.Console.WriteLine("No results found.");
            return 0;
        }

        var listingIds = result.Hits.Select(h => h.ListingId).ToList();
        var listings = await _dbContext.Listings
            .Where(l => listingIds.Contains(l.ListingId))
            .ToDictionaryAsync(l => l.ListingId, ct);

        System.Console.WriteLine($"Found {result.Hits.Count} results:");
        System.Console.WriteLine(new string('-', 80));

        foreach (var hit in result.Hits)
        {
            var listing = listings.GetValueOrDefault(hit.ListingId);
            var title = listing?.Title ?? "(not in database)";
            var price = listing?.Price?.ToString("C") ?? "N/A";
            var status = listing?.ListingStatus ?? "Unknown";

            System.Console.WriteLine($"Score: {hit.Score:F4} | {status,-10} | {price,-12} | {title}");
            System.Console.WriteLine($"  ID: {hit.ListingId}");
            System.Console.WriteLine();
        }

        return 0;
    }
}
