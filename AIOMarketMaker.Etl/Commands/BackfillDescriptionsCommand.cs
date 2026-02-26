using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AngleSharp.Html.Parser;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
namespace AIOMarketMaker.Etl.Commands;

record BackfillResult(string ListingId, string? Title, string? Description, Exception? Error);

public static class BackfillDescriptionsCommand
{
    public static async Task Run(IHost host, int? limit)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var urlBuilder = scope.ServiceProvider.GetRequiredService<IEbayUrlBuilder>();
        var listingParser = scope.ServiceProvider.GetRequiredService<IListingParser>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();

        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        Console.WriteLine("=== Backfill Pending Descriptions ===");
        Console.WriteLine();

        // Step 1: Find pending listings
        Console.Write("Loading pending listings...");
        var allPending = await db.Listings
            .AsNoTracking()
            .Where(l => l.DescriptionStatus == "pending" && l.ListingId != null)
            .OrderBy(l => l.Id)
            .Select(l => new { l.ListingId, l.Title })
            .ToListAsync();
        Console.WriteLine($" {allPending.Count:N0} found.");

        if (allPending.Count == 0)
        {
            Console.WriteLine("No pending descriptions. Nothing to do.");
            httpClient.Dispose();
            return;
        }

        var pending = limit.HasValue ? allPending.Take(limit.Value).ToList() : allPending;
        var total = pending.Count;

        // Step 2: Pre-run summary
        var embeddingBatches = (int)Math.Ceiling(total / 50.0);
        Console.WriteLine();
        Console.WriteLine($"Found:       {allPending.Count:N0} pending listings");
        Console.WriteLine($"Processing:  {total}{(limit.HasValue ? $" (--limit {limit.Value})" : " (all)")}");
        Console.WriteLine($"Concurrency: 50 parallel HTTP requests (direct to itm.ebaydesc.com)");
        Console.WriteLine();
        Console.WriteLine("Costs:");
        Console.WriteLine($"  HTTP requests:     {total} (no proxy needed)");
        Console.WriteLine($"  OpenAI embeddings: {embeddingBatches} batch(es) of up to 50 (~${embeddingBatches * 0.01m:F2})");
        Console.WriteLine($"  DB updates:        {total} rows");
        Console.WriteLine($"  Vector upserts:    up to {total}");
        Console.WriteLine();
        Console.Write("Proceed? (y/n) ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer != "y")
        {
            Console.WriteLine("Aborted.");
            httpClient.Dispose();
            return;
        }

        Console.WriteLine();

        // Bot detection
        var botKeywords = new[] { "captcha", "blocked", "security check", "access denied", "rate limit", "too many requests", "please verify" };
        var consecutiveBotDetections = 0;
        const int maxConsecutiveBotDetections = 5;
        var aborted = false;

        // Step 3: Phase 1 — Fetch descriptions (producer/consumer with SemaphoreSlim(50))
        var channel = Channel.CreateUnbounded<BackfillResult>();
        var concurrency = new SemaphoreSlim(50);
        var processed = 0;
        var fetched = 0;
        var errors = 0;
        var missing = 0;
        var botDetections = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var producerTask = Task.Run(async () =>
        {
            var fetchTasks = pending.Select(async item =>
            {
                if (aborted)
                {
                    return;
                }

                await concurrency.WaitAsync();
                string? description = null;
                Exception? error = null;
                try
                {
                    var url = urlBuilder.BuildDescriptionUrl(item.ListingId);
                    var html = await httpClient.GetStringAsync(url);

                    if (html.Length < 5_000)
                    {
                        var htmlLower = html.ToLowerInvariant();
                        if (botKeywords.Any(kw => htmlLower.Contains(kw)))
                        {
                            Interlocked.Increment(ref botDetections);
                            var consecutive = Interlocked.Increment(ref consecutiveBotDetections);
                            error = new InvalidOperationException(
                                $"Bot detection: {html.Length} bytes, keywords found");
                            if (consecutive >= maxConsecutiveBotDetections)
                            {
                                aborted = true;
                                Console.WriteLine();
                                Console.WriteLine($"  ABORT: {maxConsecutiveBotDetections} consecutive bot detections. Stopping.");
                            }
                        }
                        else
                        {
                            Interlocked.Exchange(ref consecutiveBotDetections, 0);
                            var parser = new HtmlParser();
                            var doc = await parser.ParseDocumentAsync(html);
                            description = listingParser.ParseDescription(doc);
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref consecutiveBotDetections, 0);
                        var parser = new HtmlParser();
                        var doc = await parser.ParseDocumentAsync(html);
                        description = listingParser.ParseDescription(doc);
                    }
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    concurrency.Release();
                }

                if (!aborted)
                {
                    await channel.Writer.WriteAsync(
                        new BackfillResult(item.ListingId, item.Title, description, error));
                }
            });

            await Task.WhenAll(fetchTasks);
            channel.Writer.Complete();
        });

        // Consumer: update DB sequentially, collect for embedding
        var toEmbed = new List<(string ListingId, string Text)>();
        await foreach (var result in channel.Reader.ReadAllAsync())
        {
            if (result.Error != null)
            {
                errors++;
                Console.WriteLine($"  ERROR [{result.ListingId}]: {result.Error.Message}");
                var failedListing = await db.Listings.FirstOrDefaultAsync(l => l.ListingId == result.ListingId);
                if (failedListing != null)
                {
                    failedListing.DescriptionStatus = "failed";
                    failedListing.UpdatedUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                processed++;
                continue;
            }

            var listing = await db.Listings.FirstAsync(l => l.ListingId == result.ListingId);
            if (string.IsNullOrEmpty(result.Description))
            {
                listing.DescriptionStatus = "missing";
                missing++;
            }
            else
            {
                listing.Description = result.Description;
                listing.DescriptionStatus = "complete";
                fetched++;

                var embeddingText = CommandHelpers.BuildEmbeddingText(result.Title, result.Description);
                if (!string.IsNullOrWhiteSpace(embeddingText))
                {
                    toEmbed.Add((result.ListingId, CommandHelpers.TruncateText(embeddingText, 6_000)));
                }
            }
            listing.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            processed++;
            if (processed % 50 == 0 || processed == total)
            {
                var elapsed = sw.Elapsed;
                var rate = processed / elapsed.TotalSeconds;
                var remaining = (total - processed) / rate;
                Console.WriteLine($"  {processed}/{total}: {fetched} fetched, {missing} missing, {errors} failed ({rate:F1}/sec, ETA {remaining:F0}s)");
            }
        }
        await producerTask;

        // Step 4: Phase 2 — Batch embed
        Console.WriteLine();
        Console.WriteLine($"Phase 2: Embedding {toEmbed.Count} listings in batches of 50...");

        var embedded = 0;
        var embedErrors = 0;
        var batchNum = 0;
        var batchCount = (int)Math.Ceiling(toEmbed.Count / 50.0);

        foreach (var batch in toEmbed.Chunk(50))
        {
            batchNum++;
            var texts = batch.Select(b => b.Text).ToList();
            try
            {
                var embeddings = await embeddingService.GetEmbeddings(texts);
                for (int i = 0; i < batch.Length; i++)
                {
                    if (embeddings[i] != null)
                    {
                        vectorIndex.Upsert(batch[i].ListingId, embeddings[i]);
                        embedded++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Embedding batch {batchNum} failed: {ex.Message}");
                embedErrors += batch.Length;
            }

            if (batchNum % 10 == 0 || batchNum == batchCount)
            {
                Console.WriteLine($"  Batch {batchNum}/{batchCount}: {embedded:N0} embedded");
            }
        }

        if (embedded > 0)
        {
            Console.Write("Saving vector index...");
            vectorIndex.Save();
            Console.WriteLine(" done.");
        }

        // Step 5: Summary
        sw.Stop();
        httpClient.Dispose();
        Console.WriteLine();
        Console.WriteLine("=== Results ===");
        Console.WriteLine($"Processed:    {processed} listings in {sw.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine($"Fetched:      {fetched} descriptions");
        Console.WriteLine($"Missing:      {missing} (no description on eBay)");
        Console.WriteLine($"Failed:       {errors} (fetch error)");
        Console.WriteLine($"Bot detected: {botDetections}");
        Console.WriteLine($"Embedded:     {embedded}");
        Console.WriteLine($"Embed errors: {embedErrors}");
        if (aborted)
        {
            Console.WriteLine($"ABORTED:      {total - processed} listings skipped due to bot detection");
        }
        Console.WriteLine($"Index now contains {vectorIndex.Count:N0} vectors");
    }
}
