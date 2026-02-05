using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class ScrapeJobQueueTrigger
{
    private readonly ILogger<ScrapeJobQueueTrigger> _logger;
    private readonly IScrapeJobProcessor _processor;
    private readonly EtlDbContext _db;

    public ScrapeJobQueueTrigger(
        ILogger<ScrapeJobQueueTrigger> logger,
        IScrapeJobProcessor processor,
        EtlDbContext db)
    {
        _logger = logger;
        _processor = processor;
        _db = db;
    }

    [Function("ProcessScrapeJob")]
    public async Task ProcessJob(
        [QueueTrigger("scrape-jobs", Connection = "AzureWebJobsStorage")] string messageJson)
    {
        ScrapeJobMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ScrapeJobMessage>(messageJson);
        }
        catch (JsonException)
        {
            _logger.LogError("Failed to deserialize queue message: {Message}", messageJson);
            return;
        }

        if (message == null)
        {
            _logger.LogError("Failed to deserialize queue message: {Message}", messageJson);
            return;
        }

        var run = await _db.ScrapeRuns.FindAsync(message.ScrapeRunId);
        if (run == null)
        {
            _logger.LogError("ScrapeRun {RunId} not found for queue message", message.ScrapeRunId);
            return;
        }

        var job = new ScrapeJobConfig(message.JobId, message.SearchTerm);
        await _processor.Execute(run, job);
    }
}
