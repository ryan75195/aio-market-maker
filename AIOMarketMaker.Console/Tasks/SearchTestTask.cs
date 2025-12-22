using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace AIOMarketMaker.Console.Tasks;

public class SearchTestTask : ITask
{
    private readonly ISemanticSearchService _searchService;
    private readonly EtlDbContext _dbContext;

    public string Name => "search-test";
    public string Description => "Test semantic search with a random listing. Usage: search-test [topK] [outputFile]";

    public SearchTestTask(ISemanticSearchService searchService, EtlDbContext dbContext)
    {
        _searchService = searchService;
        _dbContext = dbContext;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        var topK = args.Length > 0 && int.TryParse(args[0], out var k) ? k : 10;
        var outputFile = args.Length > 1 ? args[1] : "search-test-results.txt";

        // Get a random listing with title and description
        var totalCount = await _dbContext.Listings.CountAsync(ct);
        if (totalCount == 0)
        {
            System.Console.WriteLine("No listings in database.");
            return 1;
        }

        var randomOffset = new Random().Next(totalCount);
        var randomListing = await _dbContext.Listings
            .Where(l => l.Title != null && l.Title != "")
            .Skip(randomOffset)
            .FirstOrDefaultAsync(ct);

        if (randomListing == null)
        {
            System.Console.WriteLine("Could not find a listing with title.");
            return 1;
        }

        // Build search string from title + description
        var searchParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(randomListing.Title))
            searchParts.Add(randomListing.Title);
        if (!string.IsNullOrWhiteSpace(randomListing.Description))
            searchParts.Add(randomListing.Description);

        var searchQuery = string.Join(" ", searchParts);

        System.Console.WriteLine($"Selected listing: {randomListing.ListingId}");
        System.Console.WriteLine($"Searching with query length: {searchQuery.Length} chars");

        // Search
        var result = await _searchService.SearchAsync(searchQuery, topK: topK, ct: ct);

        // Get listing details for results
        var listingIds = result.Hits.Select(h => h.ListingId).ToList();
        var listings = await _dbContext.Listings
            .Where(l => listingIds.Contains(l.ListingId))
            .ToDictionaryAsync(l => l.ListingId, ct);

        // Build output
        var output = $"""
================================================================================
SEMANTIC SEARCH TEST RESULTS
================================================================================

INPUT LISTING:
--------------------------------------------------------------------------------
ID: {randomListing.ListingId}
Title: {randomListing.Title}
Price: {randomListing.Price:C}
Status: {randomListing.ListingStatus}

Description:
{randomListing.Description ?? "(no description)"}

SEARCH QUERY:
--------------------------------------------------------------------------------
{searchQuery}

RESULTS ({result.Hits.Count} hits):
--------------------------------------------------------------------------------

""";

        var rank = 1;
        foreach (var hit in result.Hits)
        {
            var listing = listings.GetValueOrDefault(hit.ListingId);
            var isSelf = hit.ListingId == randomListing.ListingId;
            var desc = listing?.Description != null
                ? (listing.Description.Length > 200 ? listing.Description[..200] + "..." : listing.Description)
                : null;

            output += $"""
#{rank} - Score: {hit.Score:F4}{(isSelf ? " [SELF]" : "")}
ID: {hit.ListingId}
Title: {listing?.Title ?? "(not in database)"}
Price: {listing?.Price?.ToString("C") ?? "N/A"}
Status: {listing?.ListingStatus ?? "Unknown"}
{(desc != null ? $"Description: {desc}" : "")}

""";
            rank++;
        }

        await File.WriteAllTextAsync(outputFile, output, ct);

        System.Console.WriteLine($"Results written to: {outputFile}");
        return 0;
    }
}
