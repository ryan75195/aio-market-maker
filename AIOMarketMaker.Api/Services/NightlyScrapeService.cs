using AIOMarketMaker.Core.Services.Pipeline;

namespace AIOMarketMaker.Api.Services;

public class NightlyScrapeService : BackgroundService
{
    private readonly IBatchPipelineRunner _pipeline;
    private readonly ILogger<NightlyScrapeService> _logger;

    public NightlyScrapeService(
        IBatchPipelineRunner pipeline,
        ILogger<NightlyScrapeService> logger)
    {
        _pipeline = pipeline;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next2AM = now.Date.AddHours(2);
            if (next2AM <= now)
            {
                next2AM = next2AM.AddDays(1);
            }

            var delay = next2AM - now;
            _logger.LogInformation("Next nightly scrape at {Time} ({Delay})", next2AM, delay);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunNightly(ct);
        }
    }

    public async Task RunNightly(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting nightly scrape");

        try
        {
            var batch = await _pipeline.CreateBatch("Nightly", ct);
            await _pipeline.Execute(batch.BatchId, ct);
            _logger.LogInformation("Nightly scrape completed");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Nightly scrape skipped: {Message}", ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Nightly scrape failed");
        }
    }
}
