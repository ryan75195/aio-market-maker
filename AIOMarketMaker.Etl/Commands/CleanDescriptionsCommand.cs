using System.Net;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AngleSharp.Html.Parser;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Parsers;
using AIOMarketMaker.Core.Services;
namespace AIOMarketMaker.Etl.Commands;

record CleanResult(string ListingId, string? Title, string? NewDescription, int OldDescriptionLength, Exception? Error);
record ContaminatedListing(string ListingId, string? Title, int DescLen);

public static class CleanDescriptionsCommand
{
    public static async Task Run(IHost host, int? limit)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var urlBuilder = scope.ServiceProvider.GetRequiredService<IEbayUrlBuilder>();
        var listingParser = scope.ServiceProvider.GetRequiredService<IListingParser>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
        var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();

        // Configure HTTP client with residential proxy for direct fetching
        var proxyString = configuration.GetValue<string>("ResidentialProxy");
        HttpClient httpClient;
        if (!string.IsNullOrWhiteSpace(proxyString))
        {
            var parts = proxyString.Split(':', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !int.TryParse(parts[3], out var port))
            {
                throw new InvalidOperationException(
                    "ResidentialProxy must be in format username:password:host:port");
            }

            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy($"http://{parts[2]}:{port}")
                {
                    Credentials = new NetworkCredential(parts[0], parts[1])
                }
            };
            httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            Console.WriteLine($"Proxy configured: {parts[2]}:{port}");
        }
        else
        {
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Console.WriteLine("WARNING: No proxy configured (ResidentialProxy not set). Using direct HTTP.");
        }

        Console.WriteLine("=== Clean Contaminated Descriptions ===");
        Console.WriteLine();

        // Step 1: Find contaminated listings
        Console.Write("Scanning for CSS-contaminated descriptions...");
        var allContaminated = await db.Listings
            .AsNoTracking()
            .Where(l => l.Description != null
                && (l.Description.Contains("font-size")
                    || l.Description.Contains("margin:")
                    || l.Description.Contains("{*zoom")
                    || l.Description.Contains("border-radius")
                    || l.Description.Contains("@media")))
            .OrderBy(l => l.Id)
            .Select(l => new ContaminatedListing(l.ListingId, l.Title, l.Description!.Length))
            .ToListAsync();
        Console.WriteLine($" {allContaminated.Count:N0} found.");

        if (allContaminated.Count == 0)
        {
            Console.WriteLine("No contaminated descriptions found. Nothing to do.");
            return;
        }

        var contaminated = limit.HasValue ? allContaminated.Take(limit.Value).ToList() : allContaminated;
        var total = contaminated.Count;

        // Step 2: Pre-run summary
        var embeddingBatches = (int)Math.Ceiling(total / 50.0);
        Console.WriteLine();
        Console.WriteLine($"Found:       {allContaminated.Count:N0} contaminated listings");
        Console.WriteLine($"Processing:  {total}{(limit.HasValue ? $" (--limit {limit.Value})" : " (all)")}");
        Console.WriteLine($"Concurrency: 50 parallel HTTP requests (direct, no browser)");
        Console.WriteLine();
        Console.WriteLine("Costs:");
        Console.WriteLine($"  HTTP requests:     {total} (via proxy)");
        Console.WriteLine($"  OpenAI embeddings: {embeddingBatches} batch(es) of up to 50 (~${embeddingBatches * 0.01m:F2})");
        Console.WriteLine($"  DB updates:        {total} rows");
        Console.WriteLine($"  Vector upserts:    {total}");
        Console.WriteLine();
        Console.Write("Proceed? (y/n) ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response != "y")
        {
            Console.WriteLine("Aborted.");
            return;
        }

        Console.WriteLine();

        // Bot detection keywords
        var botKeywords = new[] { "captcha", "blocked", "security check", "access denied", "rate limit", "too many requests", "please verify" };
        var consecutiveBotDetections = 0;
        const int maxConsecutiveBotDetections = 5;
        var aborted = false;

        // Step 3: Phase 1 — Fetch & update (producer/consumer with SemaphoreSlim(50))
        var channel = Channel.CreateUnbounded<CleanResult>();
        var concurrency = new SemaphoreSlim(50);
        var processed = 0;
        var cleaned = 0;
        var errors = 0;
        var botDetections = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var oldLengths = new List<int>();
        var newLengths = new List<int>();

        var producerTask = Task.Run(async () =>
        {
            var fetchTasks = contaminated.Select(async item =>
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

                    // Bot detection: check for suspiciously small responses with bot keywords
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
                            return;
                        }
                    }

                    // Reset consecutive counter on success
                    Interlocked.Exchange(ref consecutiveBotDetections, 0);

                    var parser = new HtmlParser();
                    var doc = await parser.ParseDocumentAsync(html);
                    description = listingParser.ParseDescription(doc);
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
                        new CleanResult(item.ListingId, item.Title, description, item.DescLen, error));
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
                processed++;
                continue;
            }

            var listing = await db.Listings.FirstAsync(l => l.ListingId == result.ListingId);
            listing.Description = result.NewDescription;
            listing.DescriptionStatus = result.NewDescription != null ? "complete" : "missing";
            listing.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // Track lengths for summary
            oldLengths.Add(result.OldDescriptionLength);
            if (result.NewDescription != null)
            {
                newLengths.Add(result.NewDescription.Length);
            }

            // Collect for Phase 2 embedding
            var embeddingText = CommandHelpers.BuildEmbeddingText(result.Title, result.NewDescription);
            if (!string.IsNullOrWhiteSpace(embeddingText))
            {
                toEmbed.Add((result.ListingId, CommandHelpers.TruncateText(embeddingText, 6_000)));
            }

            processed++;
            if (result.NewDescription?.Length != result.OldDescriptionLength)
            {
                cleaned++;
            }

            if (processed % 10 == 0 || processed == total)
            {
                var elapsed = sw.Elapsed;
                var rate = processed / elapsed.TotalSeconds;
                var remaining = (total - processed) / rate;
                Console.WriteLine($"  {processed}/{total}: {cleaned} cleaned, {errors} failed ({rate:F1}/sec, ETA {remaining:F0}s)");
            }
        }
        await producerTask;

        // Step 4: Phase 2 — Batch re-embed
        Console.WriteLine();
        Console.WriteLine($"Phase 2: Re-embedding {toEmbed.Count} listings in batches of 50...");

        var embedded = 0;
        var embedErrors = 0;
        foreach (var batch in toEmbed.Chunk(50))
        {
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
                Console.WriteLine($"  Embedding batch failed: {ex.Message}");
                embedErrors += batch.Length;
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
        Console.WriteLine($"Cleaned:      {cleaned} (description updated)");
        Console.WriteLine($"Unchanged:    {processed - cleaned - errors} (description was already clean after re-parse)");
        Console.WriteLine($"Failed:       {errors} (fetch error)");
        Console.WriteLine($"Bot detected: {botDetections}");
        Console.WriteLine($"Embedded:     {embedded}");
        if (aborted)
        {
            Console.WriteLine($"ABORTED:      {total - processed} listings skipped due to bot detection");
        }

        if (oldLengths.Count > 0 && newLengths.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"Before: avg {oldLengths.Average():N0} chars (with CSS noise)");
            Console.WriteLine($"After:  avg {newLengths.Average():N0} chars (clean text)");
        }
    }
}
