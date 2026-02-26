using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Console.Tasks;

public class ReindexMissingTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorIndex _vectorIndex;

    public string Name => "reindex-missing";
    public string Description => "Re-index listings missing from the vector index";

    public ReindexMissingTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        IEmbeddingService embeddingService,
        IVectorIndex vectorIndex)
    {
        _dbFactory = dbFactory;
        _embeddingService = embeddingService;
        _vectorIndex = vectorIndex;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        System.Console.WriteLine("=== Re-index Missing Listings ===");
        System.Console.WriteLine($"Current index size: {_vectorIndex.Count:N0} vectors");
        System.Console.WriteLine();

        // Load all listings that are missing from the index
        System.Console.Write("Loading listings from database...");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var allListings = await db.Listings
            .AsNoTracking()
            .Where(l => l.Title != null)
            .Select(l => new { l.ListingId, l.Title, l.Description, l.ListingStatus })
            .ToListAsync(ct);
        System.Console.WriteLine($" {allListings.Count:N0} listings.");

        var missing = allListings.Where(l => !_vectorIndex.Contains(l.ListingId)).ToList();
        System.Console.WriteLine($"Missing from index: {missing.Count:N0}");
        System.Console.WriteLine($"  Active: {missing.Count(l => l.ListingStatus == "Active"):N0}");
        System.Console.WriteLine($"  Sold:   {missing.Count(l => l.ListingStatus == "Sold"):N0}");

        if (missing.Count == 0)
        {
            System.Console.WriteLine("Nothing to do — all listings are indexed.");
            return 0;
        }

        System.Console.WriteLine();

        // Build embedding text for each (same logic as ListingIndexingService.BuildEmbeddingText)
        // Truncate to 6000 chars to stay well under text-embedding-3-large's 8192 token limit
        // (eBay HTML descriptions tokenize at ~3-4 chars/token, so 6000 chars = 1500-2000 tokens)
        const int maxChars = 6_000;
        var items = missing
            .Select(l => new
            {
                l.ListingId,
                Text = CommandHelpers.TruncateText(
                    string.Join(" ", new[] { l.Title, l.Description }.Where(s => !string.IsNullOrWhiteSpace(s))),
                    maxChars)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .ToList();

        System.Console.WriteLine($"Embedding {items.Count:N0} listings in batches of 50...");

        var batchSize = 50;
        var embedded = 0;
        var errors = 0;
        var batches = items.Chunk(batchSize);
        var batchCount = (int)Math.Ceiling(items.Count / (double)batchSize);
        var batchNum = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        foreach (var batch in batches)
        {
            batchNum++;
            var texts = batch.Select(b => b.Text).ToList();
            float[][] embeddings;
            try
            {
                embeddings = await _embeddingService.GetEmbeddings(texts);
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"  Batch {batchNum} failed ({ex.Message}), falling back to individual...");
                embeddings = new float[batch.Length][];
                for (int j = 0; j < batch.Length; j++)
                {
                    try
                    {
                        embeddings[j] = await _embeddingService.GetEmbedding(batch[j].Text);
                    }
                    catch
                    {
                        errors++;
                    }
                }
            }

            for (int i = 0; i < batch.Length; i++)
            {
                if (embeddings[i] != null)
                {
                    _vectorIndex.Upsert(batch[i].ListingId, embeddings[i]);
                    embedded++;
                }
            }

            if (batchNum % 10 == 0 || batchNum == batchCount)
            {
                var elapsed = sw.Elapsed;
                var rate = embedded / elapsed.TotalSeconds;
                var remaining = (items.Count - embedded) / rate;
                System.Console.WriteLine($"  Batch {batchNum}/{batchCount}: {embedded:N0} embedded ({rate:F0}/sec, ~{remaining:F0}s remaining)");
            }
        }

        // Save the updated index
        System.Console.Write("Saving index...");
        _vectorIndex.Save();
        System.Console.WriteLine(" done.");
        System.Console.WriteLine();
        System.Console.WriteLine($"Re-indexed {embedded:N0} listings in {sw.Elapsed.TotalSeconds:F0}s");
        if (errors > 0)
        {
            System.Console.WriteLine($"Failed to embed {errors:N0} listings (likely exceeding token limits)");
        }
        System.Console.WriteLine($"Index now contains {_vectorIndex.Count:N0} vectors");

        return 0;
    }
}
