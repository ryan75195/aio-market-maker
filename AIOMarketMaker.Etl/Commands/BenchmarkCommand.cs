using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Etl.Commands;

public static class BenchmarkCommand
{
    public static async Task Run(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var vectorIndex = scope.ServiceProvider.GetRequiredService<IVectorIndex>();

        // Pinecone credentials for comparison
        var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey")
            ?? configuration.GetValue<string>("Values:Pinecone:ApiKey")
            ?? throw new InvalidOperationException("Pinecone:ApiKey required for benchmark");
        var pineconeHost = configuration.GetValue<string>("Pinecone:Host")
            ?? configuration.GetValue<string>("Values:Pinecone:Host")
            ?? "arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io";

        Console.WriteLine("=== Vector Search Benchmark: Local USearch vs Pinecone Cloud ===");
        Console.WriteLine($"Local index: {vectorIndex.Count:N0} vectors");
        Console.WriteLine($"Pinecone host: {pineconeHost}");
        Console.WriteLine();

        // Pick 20 random listings that exist in the local index
        var sampleIds = await db.Listings
            .AsNoTracking()
            .Where(l => l.ListingStatus == "Active")
            .OrderBy(l => Guid.NewGuid())
            .Select(l => l.ListingId)
            .Take(100)
            .ToListAsync();

        // Filter to only those in the local index
        var testIds = sampleIds.Where(id => vectorIndex.Contains(id)).Take(20).ToList();
        Console.WriteLine($"Testing with {testIds.Count} listings");
        Console.WriteLine();

        // Set up Pinecone HTTP client
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Api-Key", pineconeApiKey);
        httpClient.BaseAddress = new Uri($"https://{pineconeHost}");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var localTimes = new List<double>();
        var pineconeTimes = new List<double>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Console.WriteLine($"{"#",-3} {"ListingId",-16} {"Local (ms)",10} {"Pinecone (ms)",14} {"Local Hits",11} {"PC Hits",8} {"Top Match",10}");
        Console.WriteLine(new string('-', 80));

        for (int i = 0; i < testIds.Count; i++)
        {
            var id = testIds[i];

            // Local USearch query
            sw.Restart();
            var localResults = vectorIndex.SearchById(id, 50).ToList();
            sw.Stop();
            var localMs = sw.Elapsed.TotalMilliseconds;
            localTimes.Add(localMs);

            // Pinecone query: first fetch the vector, then query
            sw.Restart();

            // Fetch vector from Pinecone
            var fetchUrl = $"/vectors/fetch?ids={Uri.EscapeDataString(id)}";
            var fetchResponse = await httpClient.GetAsync(fetchUrl);
            fetchResponse.EnsureSuccessStatusCode();
            var fetchJson = await fetchResponse.Content.ReadAsStringAsync();
            var fetchData = JsonSerializer.Deserialize<PineconeFetchResponse>(fetchJson, jsonOptions);
            var vector = fetchData?.Vectors?.GetValueOrDefault(id)?.Values;

            int pineconeHits = 0;
            float pineconeTopScore = 0;
            if (vector != null)
            {
                // Query Pinecone with the vector
                var queryBody = JsonSerializer.Serialize(new
                {
                    vector = vector,
                    topK = 50,
                    includeValues = false
                });
                var queryResponse = await httpClient.PostAsync("/query",
                    new StringContent(queryBody, System.Text.Encoding.UTF8, "application/json"));
                queryResponse.EnsureSuccessStatusCode();
                var queryJson = await queryResponse.Content.ReadAsStringAsync();
                var queryData = JsonSerializer.Deserialize<JsonElement>(queryJson);

                if (queryData.TryGetProperty("matches", out var matches))
                {
                    pineconeHits = matches.GetArrayLength();
                    if (pineconeHits > 0)
                    {
                        pineconeTopScore = matches[0].GetProperty("score").GetSingle();
                    }
                }
            }
            sw.Stop();
            var pineconeMs = sw.Elapsed.TotalMilliseconds;
            pineconeTimes.Add(pineconeMs);

            var localTopScore = localResults.FirstOrDefault()?.Score ?? 0;
            Console.WriteLine($"{i + 1,-3} {id,-16} {localMs,10:F3} {pineconeMs,14:F1} {localResults.Count,11} {pineconeHits,8} {(localTopScore == pineconeTopScore ? "match" : $"L:{localTopScore:F4} P:{pineconeTopScore:F4}"),10}");
        }

        Console.WriteLine(new string('-', 80));
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        Console.WriteLine($"{"Metric",-25} {"Local USearch",15} {"Pinecone Cloud",15} {"Speedup",10}");
        Console.WriteLine(new string('-', 65));
        Console.WriteLine($"{"Mean (ms)",-25} {localTimes.Average(),15:F3} {pineconeTimes.Average(),15:F1} {pineconeTimes.Average() / localTimes.Average(),10:F0}x");
        Console.WriteLine($"{"Median (ms)",-25} {Median(localTimes),15:F3} {Median(pineconeTimes),15:F1} {Median(pineconeTimes) / Median(localTimes),10:F0}x");
        Console.WriteLine($"{"Min (ms)",-25} {localTimes.Min(),15:F3} {pineconeTimes.Min(),15:F1}");
        Console.WriteLine($"{"Max (ms)",-25} {localTimes.Max(),15:F3} {pineconeTimes.Max(),15:F1}");
        Console.WriteLine($"{"P95 (ms)",-25} {Percentile(localTimes, 95),15:F3} {Percentile(pineconeTimes, 95),15:F1}");
        Console.WriteLine();

        var totalLocal = localTimes.Sum();
        var totalPinecone = pineconeTimes.Sum();
        Console.WriteLine($"Total for {testIds.Count} queries: Local {totalLocal:F1}ms vs Pinecone {totalPinecone:F0}ms");
        Console.WriteLine($"Projected for 114K ETL queries: Local {localTimes.Average() * 114000 / 1000:F0}s vs Pinecone {pineconeTimes.Average() * 114000 / 1000 / 60:F0}min");
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private static double Percentile(List<double> values, int percentile)
    {
        var sorted = values.OrderBy(v => v).ToList();
        int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, index)];
    }
}
