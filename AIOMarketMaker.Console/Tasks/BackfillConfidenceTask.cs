using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Console.Tasks;

public class BackfillConfidenceTask : ITask
{
    private readonly IDbContextFactory<EtlDbContext> _dbFactory;
    private readonly IVariantClassifierClient _classifier;

    public string Name => "backfill-confidence";
    public string Description => "Backfill ClassifierConfidence + IsComparable on all ListingRelationships";

    public BackfillConfidenceTask(
        IDbContextFactory<EtlDbContext> dbFactory,
        IVariantClassifierClient classifier)
    {
        _dbFactory = dbFactory;
        _classifier = classifier;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        const int classifyBatchSize = 256;
        const int relBatchSize = 10_000;

        // Catch native crashes that bypass managed exception handling
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            System.Console.Error.WriteLine($"UNHANDLED: {e.ExceptionObject}");
            System.Console.Error.Flush();
        };

        System.Console.WriteLine("=== Backfill ClassifierConfidence + IsComparable (Ensemble) ===");
        System.Console.WriteLine();

        // Preload all listings into memory (titles + descriptions for classifier input)
        System.Console.Write("Loading all listings into memory...");
        await using var loadDb = await _dbFactory.CreateDbContextAsync(ct);
        var allListings = await loadDb.Listings
            .AsNoTracking()
            .Select(l => new { l.Id, l.Title, l.Description })
            .ToDictionaryAsync(l => l.Id, ct);
        System.Console.WriteLine($" {allListings.Count:N0} listings loaded.");

        // Count total relationships
        var totalRelationships = await loadDb.ListingRelationships.AsNoTracking().CountAsync(ct);
        var startFromId = CommandHelpers.GetIntArg(args, "--start-from-id") ?? 0;

        var totalToBackfill = startFromId > 0
            ? await loadDb.ListingRelationships.AsNoTracking().CountAsync(r => r.Id > startFromId, ct)
            : totalRelationships;

        System.Console.WriteLine($"Total relationships: {totalRelationships:N0}");
        System.Console.WriteLine($"To process: {totalToBackfill:N0}");
        if (startFromId > 0)
        {
            System.Console.WriteLine($"Resuming from ID: {startFromId:N0}");
        }
        System.Console.WriteLine($"Classify batch size: {classifyBatchSize}");
        System.Console.WriteLine();

        var limit = CommandHelpers.GetIntArg(args, "--limit");

        // Warmup: classify a single dummy pair to verify CUDA works before starting bulk work
        System.Console.Write("Warmup: testing classifier with 1 pair...");
        System.Console.Out.Flush();
        try
        {
            var warmupPair = new ClassifyPairRequest("Test Item A", "Description A", "Test Item B", "Description B", 0.9f);
            var warmupResult = await _classifier.Classify(new[] { warmupPair });
            System.Console.WriteLine($" OK (confidence={warmupResult[0].Confidence:F3})");
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($" FAILED: {ex.GetType().Name}: {ex.Message}");
            System.Console.WriteLine("Cannot proceed without a working classifier.");
            return 1;
        }

        var processed = 0;
        var updated = 0;
        var errors = 0;
        var lastProcessedId = startFromId;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sanityCheckDone = false;
        var nextCheckpointAt = 500_000;
        var runningAbove95 = 0;
        var runningAbove90 = 0;
        var runningAbove80 = 0;
        var runningAbove50 = 0;
        var runningBelow50 = 0;

        // Accumulate updates and flush every SaveEveryNPairs (mirrors ComparablesEtlService)
        const int saveEveryNPairs = 5120;
        var pendingUpdates = new List<(int Id, float Confidence, bool IsComparable)>();

        try
        {
        while (true)
        {
            if (limit.HasValue && updated >= limit.Value)
            {
                break;
            }

            // Load relationships by ID (uses clustered index - instant)
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var relBatch = await db.ListingRelationships
                .AsNoTracking()
                .Where(r => r.Id > lastProcessedId)
                .OrderBy(r => r.Id)
                .Take(relBatchSize)
                .Select(r => new { r.Id, r.ListingIdA, r.ListingIdB, r.SimilarityScore })
                .ToListAsync(ct);

            if (relBatch.Count == 0)
            {
                break;
            }

            lastProcessedId = relBatch[^1].Id;

            // Process in classify-sized chunks
            foreach (var chunk in relBatch.Chunk(classifyBatchSize))
            {
                var requests = new List<ClassifyPairRequest>();
                var validIndices = new List<int>();

                for (var i = 0; i < chunk.Length; i++)
                {
                    var rel = chunk[i];
                    if (!allListings.TryGetValue(rel.ListingIdA, out var a) ||
                        !allListings.TryGetValue(rel.ListingIdB, out var b))
                    {
                        errors++;
                        continue;
                    }

                    requests.Add(new ClassifyPairRequest(
                        a.Title ?? "", a.Description ?? "",
                        b.Title ?? "", b.Description ?? "",
                        (float?)rel.SimilarityScore));
                    validIndices.Add(i);
                }

                if (requests.Count == 0)
                {
                    processed += chunk.Length;
                    continue;
                }

                // Classify
                IReadOnlyList<PairResult> results;
                try
                {
                    results = await _classifier.Classify(requests);
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"  Classifier error at ID {chunk[0].Id}: {ex.GetType().Name}: {ex.Message}");
                    errors += chunk.Length;
                    processed += chunk.Length;
                    continue;
                }

                // Accumulate results
                for (var i = 0; i < results.Count; i++)
                {
                    var conf = results[i].Confidence;
                    pendingUpdates.Add((chunk[validIndices[i]].Id, conf, results[i].IsComparable));

                    if (conf >= 0.95f) { runningAbove95++; }
                    else if (conf >= 0.90f) { runningAbove90++; }
                    else if (conf >= 0.80f) { runningAbove80++; }
                    else if (conf >= 0.50f) { runningAbove50++; }
                    else { runningBelow50++; }
                }

                processed += chunk.Length;

                // Sanity check after first ~1,000 pairs
                if (!sanityCheckDone && pendingUpdates.Count >= 1000)
                {
                    sanityCheckDone = true;
                    var confs = pendingUpdates.Select(u => u.Confidence).ToList();
                    var above95 = confs.Count(c => c >= 0.95f);
                    var above90 = confs.Count(c => c >= 0.90f && c < 0.95f);
                    var above80 = confs.Count(c => c >= 0.80f && c < 0.90f);
                    var below80 = confs.Count(c => c < 0.80f);
                    var comparable = pendingUpdates.Count(u => u.IsComparable);

                    System.Console.WriteLine();
                    System.Console.WriteLine("=== SANITY CHECK (first 1,000 pairs) ===");
                    System.Console.WriteLine($"  0.95+:     {above95,5} ({100.0 * above95 / confs.Count:F1}%)");
                    System.Console.WriteLine($"  0.90-0.95: {above90,5} ({100.0 * above90 / confs.Count:F1}%)");
                    System.Console.WriteLine($"  0.80-0.90: {above80,5} ({100.0 * above80 / confs.Count:F1}%)");
                    System.Console.WriteLine($"  < 0.80:    {below80,5} ({100.0 * below80 / confs.Count:F1}%)");
                    System.Console.WriteLine($"  IsComparable=true: {comparable} ({100.0 * comparable / confs.Count:F1}%)");
                    System.Console.WriteLine($"  Mean: {confs.Average():F4}  Min: {confs.Min():F4}  Max: {confs.Max():F4}");
                    System.Console.WriteLine();

                    // Expected: ~75% at 0.95+, ~15% at 0.90-0.95, ~7% at 0.80-0.90
                    // Broken: ~97% at 0.95+ (all above 0.5)
                    if (above95 > confs.Count * 0.90)
                    {
                        System.Console.WriteLine("*** WARNING: >90% at 0.95+ — looks like the broken distribution! ***");
                        System.Console.WriteLine("*** Check ensemble weights are loaded correctly. ***");
                    }
                    else
                    {
                        System.Console.WriteLine("Distribution looks healthy. Continuing...");
                    }
                    System.Console.WriteLine();
                }

                // Flush to DB every saveEveryNPairs (mirrors ComparablesEtlService pattern)
                if (pendingUpdates.Count >= saveEveryNPairs)
                {
                    await FlushPendingUpdates(pendingUpdates, ct);
                    updated += pendingUpdates.Count;
                    pendingUpdates.Clear();
                }

                // Progress logging every ~10,240 pairs (matches ComparablesEtlService)
                var totalProcessed = updated + pendingUpdates.Count;
                if (totalProcessed % 10240 < classifyBatchSize)
                {
                    var elapsed = sw.Elapsed;
                    var rate = totalProcessed / elapsed.TotalSeconds;
                    var remaining = (totalToBackfill - totalProcessed) / Math.Max(rate, 0.1);
                    var eta = TimeSpan.FromSeconds(remaining);

                    System.Console.WriteLine(
                        $"  {totalProcessed:N0}/{totalToBackfill:N0} updated | {errors:N0} errors | " +
                        $"{rate:F0} pairs/sec | ETA {eta.Hours}h {eta.Minutes:D2}m {eta.Seconds:D2}s | " +
                        $"last ID {lastProcessedId:N0}");

                    // Distribution checkpoint every 500K
                    if (totalProcessed >= nextCheckpointAt)
                    {
                        var total = runningAbove95 + runningAbove90 + runningAbove80 + runningAbove50 + runningBelow50;
                        System.Console.WriteLine($"  [CHECKPOINT @ {totalProcessed:N0}] " +
                            $"0.95+: {100.0 * runningAbove95 / total:F1}% | " +
                            $"0.90-0.95: {100.0 * runningAbove90 / total:F1}% | " +
                            $"0.80-0.90: {100.0 * runningAbove80 / total:F1}% | " +
                            $"0.50-0.80: {100.0 * runningAbove50 / total:F1}% | " +
                            $"<0.50: {100.0 * runningBelow50 / total:F1}%");
                        nextCheckpointAt += 500_000;
                    }
                }
            }
        }

        // Flush remaining
        if (pendingUpdates.Count > 0)
        {
            await FlushPendingUpdates(pendingUpdates, ct);
            updated += pendingUpdates.Count;
            pendingUpdates.Clear();
        }
        }
        catch (Exception ex)
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);

            // Flush whatever we have so we don't lose work
            if (pendingUpdates.Count > 0)
            {
                System.Console.WriteLine($"Flushing {pendingUpdates.Count} pending updates before exit...");
                try
                {
                    await FlushPendingUpdates(pendingUpdates, ct);
                    updated += pendingUpdates.Count;
                    System.Console.WriteLine("Flush succeeded.");
                }
                catch (Exception flushEx)
                {
                    System.Console.WriteLine($"Flush failed: {flushEx.Message}");
                }
            }
        }

        sw.Stop();
        System.Console.WriteLine();
        System.Console.WriteLine("=== Summary ===");
        System.Console.WriteLine($"Scanned:    {processed:N0}");
        System.Console.WriteLine($"Updated:    {updated:N0}");
        System.Console.WriteLine($"Errors:     {errors:N0}");
        System.Console.WriteLine($"Last ID:    {lastProcessedId:N0}");
        System.Console.WriteLine($"Duration:   {sw.Elapsed.Hours}h {sw.Elapsed.Minutes:D2}m {sw.Elapsed.Seconds:D2}s");
        System.Console.WriteLine($"Rate:       {(sw.Elapsed.TotalSeconds > 0 ? updated / sw.Elapsed.TotalSeconds : 0):F0} pairs/sec");

        return 0;
    }

    private async Task FlushPendingUpdates(List<(int Id, float Confidence, bool IsComparable)> updates, CancellationToken ct)
    {
        // SQL Server's query optimizer can't handle huge CASE statements (>1000 rows blows the stack).
        // Chunk into 512-row SQL statements.
        const int sqlChunkSize = 512;

        await using var updateDb = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var chunk in updates.Chunk(sqlChunkSize))
        {
            var sb = new StringBuilder();
            sb.Append("UPDATE ListingRelationships SET ClassifierConfidence = CASE Id ");
            var ids = new List<string>(chunk.Length);
            foreach (var (id, confidence, _) in chunk)
            {
                sb.Append($"WHEN {id} THEN {confidence.ToString(CultureInfo.InvariantCulture)} ");
                ids.Add(id.ToString());
            }
            sb.Append("END, IsComparable = CASE Id ");
            foreach (var (id, _, isComparable) in chunk)
            {
                sb.Append($"WHEN {id} THEN {(isComparable ? 1 : 0)} ");
            }
            sb.Append($"END WHERE Id IN ({string.Join(",", ids)})");
            await updateDb.Database.ExecuteSqlRawAsync(sb.ToString(), ct);
        }
    }
}
