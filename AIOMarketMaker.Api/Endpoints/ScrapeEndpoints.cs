using AIOMarketMaker.Core.Services.Pipeline;

namespace AIOMarketMaker.Api.Endpoints;

public record StartScrapeResponse(Guid BatchId, int RunCount);
public record NoJobsResponse(string Message);

public static class ScrapeEndpoints
{
    public static void MapScrapeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/scrape/start", StartScrape);
    }

    private static async Task<IResult> StartScrape(
        IBatchPipelineRunner pipeline,
        ILogger<Program> logger)
    {
        BatchCreationResult batch;
        try
        {
            batch = await pipeline.CreateBatch("Manual");
        }
        catch (InvalidOperationException ex)
        {
            return Results.Ok(new NoJobsResponse(ex.Message));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await pipeline.Execute(batch.BatchId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background batch execution failed for {BatchId}", batch.BatchId);
            }
        });

        return Results.Accepted(value: new StartScrapeResponse(batch.BatchId, batch.RunCount));
    }
}
