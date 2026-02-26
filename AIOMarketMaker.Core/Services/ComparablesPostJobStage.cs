using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Core.Services;

public class ComparablesPostJobStage : IPostJobStage
{
    private readonly IComparablesEtlService _etlService;
    private readonly ILogger<ComparablesPostJobStage> _logger;

    public string Name => "Finding Comparables";

    public ComparablesPostJobStage(
        IComparablesEtlService etlService,
        ILogger<ComparablesPostJobStage> logger)
    {
        _etlService = etlService;
        _logger = logger;
    }

    public async Task Execute(PostJobContext context, CancellationToken ct = default)
    {
        _logger.LogInformation("Running comparables ETL for RunId={RunId}, JobId={JobId}", context.RunId, context.JobId);

        var result = await _etlService.RunForJob(context.JobId, ct);

        _logger.LogInformation(
            "Comparables ETL complete for RunId={RunId}: {Processed} processed, {Pairs} pairs, {Comps} comparables found",
            context.RunId, result.ListingsProcessed, result.CandidatePairsFound, result.ComparablesFound);
    }
}
