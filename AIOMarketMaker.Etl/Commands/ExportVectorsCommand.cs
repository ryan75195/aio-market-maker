using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Etl.Commands;

record PineconeFetchResponse(Dictionary<string, PineconeVector>? Vectors);
record PineconeVector(string Id, float[]? Values);

public static class ExportVectorsCommand
{
    public static async Task Run(IHost host, string[] args)
    {
        using var scope = host.Services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var db = scope.ServiceProvider.GetRequiredService<EtlDbContext>();
        var vectorIndexConfig = scope.ServiceProvider.GetRequiredService<VectorIndexConfig>();

        // Pinecone credentials (required for export)
        var pineconeApiKey = configuration.GetValue<string>("Pinecone:ApiKey")
            ?? configuration.GetValue<string>("Values:Pinecone:ApiKey")
            ?? throw new InvalidOperationException("Pinecone:ApiKey is required for export. Add it to local.settings.json.");
        var pineconeHost = configuration.GetValue<string>("Pinecone:Host")
            ?? configuration.GetValue<string>("Values:Pinecone:Host")
            ?? "arbitrage-d207f30.svc.aped-4627-b74a.pinecone.io";

        Console.WriteLine($"Export vectors from Pinecone host '{pineconeHost}' to local USearch index");
        Console.WriteLine($"  Index path: {vectorIndexConfig.IndexPath}");
        Console.WriteLine($"  ID map path: {vectorIndexConfig.IdMapPath}");
        Console.WriteLine();

        // Create HttpClient for Pinecone REST API (no SDK needed)
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Api-Key", pineconeApiKey);
        httpClient.BaseAddress = new Uri($"https://{pineconeHost}");
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Load all listing IDs from database
        Console.Write("Loading listing IDs from database...");
        var listingIds = await db.Listings
            .AsNoTracking()
            .Select(l => l.ListingId)
            .ToListAsync();
        Console.WriteLine($" {listingIds.Count:N0} listings.");

        // Create the local USearch index
        using var localIndex = new USearchVectorIndex(vectorIndexConfig);

        var batchSize = 100; // Keep URL under 8KB (12-char IDs × 100 ≈ 2KB query string)
        var exported = 0;
        var missing = 0;
        var batches = listingIds.Chunk(batchSize);
        var batchCount = (int)Math.Ceiling(listingIds.Count / (double)batchSize);
        var batchNum = 0;

        Console.WriteLine($"Fetching {listingIds.Count:N0} vectors in {batchCount} batches of {batchSize}...");
        Console.WriteLine();

        foreach (var batch in batches)
        {
            batchNum++;
            var queryString = string.Join("&", batch.Select(id => $"ids={Uri.EscapeDataString(id)}"));
            var httpResponse = await httpClient.GetAsync($"/vectors/fetch?{queryString}");
            httpResponse.EnsureSuccessStatusCode();
            var json = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonSerializer.Deserialize<PineconeFetchResponse>(json, jsonOptions);

            if (response?.Vectors != null)
            {
                foreach (var (id, vector) in response.Vectors)
                {
                    if (vector.Values != null)
                    {
                        localIndex.Upsert(id, vector.Values);
                        exported++;
                    }
                    else
                    {
                        missing++;
                    }
                }
            }

            var batchMissing = batch.Length - (response?.Vectors?.Count ?? 0);
            missing += batchMissing;

            if (batchNum % 100 == 0 || batchNum == batchCount)
            {
                Console.WriteLine($"  Batch {batchNum}/{batchCount}: {exported:N0} exported, {missing:N0} missing");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Export complete: {exported:N0} vectors exported, {missing:N0} not found in Pinecone");

        // Save to disk
        Console.Write($"Saving index to {vectorIndexConfig.IndexPath}...");
        localIndex.Save();
        Console.WriteLine(" done.");

        // Report file sizes
        var indexSize = new FileInfo(vectorIndexConfig.IndexPath).Length;
        var idMapSize = new FileInfo(vectorIndexConfig.IdMapPath).Length;
        Console.WriteLine($"  Index file: {indexSize / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  ID map file: {idMapSize / 1024.0 / 1024.0:F1} MB");
        Console.WriteLine($"  Total vectors in index: {localIndex.Count:N0}");
    }
}
