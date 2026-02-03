using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class ScrapeJobQueueTrigger
{
    private readonly ILogger<ScrapeJobQueueTrigger> _logger;
    private readonly IScrapeJobProcessor _processor;

    public ScrapeJobQueueTrigger(
        ILogger<ScrapeJobQueueTrigger> logger,
        IScrapeJobProcessor processor)
    {
        _logger = logger;
        _processor = processor;
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

        await _processor.Process(message);
    }
}
