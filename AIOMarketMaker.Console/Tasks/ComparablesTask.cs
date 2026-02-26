using AIOMarketMaker.Core.Services;

namespace AIOMarketMaker.Console.Tasks;

public class ComparablesTask : ITask
{
    private readonly IComparablesEtlService _etl;

    public string Name => "comparables";
    public string Description => "Find comparable listings via vector search + ONNX classification";

    public ComparablesTask(IComparablesEtlService etl)
    {
        _etl = etl;
    }

    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        var dryRun = args.Contains("--dry-run");
        var result = await _etl.Run(dryRun);

        System.Console.WriteLine();
        System.Console.WriteLine(dryRun ? "Dry Run Summary" : "Run Summary");
        System.Console.WriteLine("===============");
        System.Console.WriteLine($"Listings processed:     {result.ListingsProcessed}");
        System.Console.WriteLine($"Vector queries:         {result.VectorQueries}");
        System.Console.WriteLine($"Candidate pairs found:  {result.CandidatePairsFound}");
        System.Console.WriteLine($"Cache hits:             {result.CacheHits}");
        System.Console.WriteLine($"ONNX pairs classified:  {result.LlmCallsMade}");
        System.Console.WriteLine($"Comparables found:      {result.ComparablesFound}");
        System.Console.WriteLine();
        System.Console.WriteLine("Predictions are computed live via ListingPredictionService.");

        return 0;
    }
}
