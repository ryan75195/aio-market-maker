using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AIOMarketMaker.Core.Services;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Etl.Commands;

public static class ComparablesCommand
{
    public static async Task Run(IHost host, string[] args)
    {
        using var scope = host.Services.CreateScope();
        var etl = scope.ServiceProvider.GetRequiredService<IComparablesEtlService>();
        var dryRun = args.Contains("--dry-run");
        var result = await etl.Run(dryRun);

        Console.WriteLine();
        Console.WriteLine(dryRun ? "Dry Run Summary" : "Run Summary");
        Console.WriteLine("===============");
        Console.WriteLine($"Listings processed:     {result.ListingsProcessed}");
        Console.WriteLine($"Vector queries:         {result.VectorQueries}");
        Console.WriteLine($"Candidate pairs found:  {result.CandidatePairsFound}");
        Console.WriteLine($"Cache hits:             {result.CacheHits}");
        Console.WriteLine($"ONNX pairs classified:  {result.LlmCallsMade}");
        Console.WriteLine($"Comparables found:      {result.ComparablesFound}");
        Console.WriteLine();
        Console.WriteLine("Predictions are computed live via ListingPredictionService.");
    }
}
