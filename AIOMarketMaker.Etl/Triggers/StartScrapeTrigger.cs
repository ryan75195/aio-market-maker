using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using AIOMarketMaker.Etl.Orchestrators;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Core.Data;
using AIOMarketMaker.Core.Data.Models;

namespace AIOMarketMaker.Etl.Triggers;

public class StartScrapeTrigger
{
    private readonly ILogger<StartScrapeTrigger> _logger;
    private readonly EtlDbContext _dbContext;

    public StartScrapeTrigger(ILogger<StartScrapeTrigger> logger, EtlDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    [Function("StartScrape")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "scrape/start")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        _logger.LogInformation("Scrape start trigger fired at {Time}", DateTime.UtcNow);

        // Parse optional request body
        StartScrapeRequest? scrapeRequest = null;
        var requestBody = await req.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(requestBody))
        {
            try
            {
                scrapeRequest = JsonSerializer.Deserialize<StartScrapeRequest>(requestBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse request body, using defaults");
            }
        }

        // Create ScrapeRun record
        var scrapeRun = new ScrapeRun
        {
            InstanceId = null, // Will be set after orchestration starts
            TriggerType = "Manual",
            StartedUtc = DateTime.UtcNow,
            Status = "Running"
        };
        _dbContext.ScrapeRuns.Add(scrapeRun);
        await _dbContext.SaveChangesAsync();

        // Start orchestration with runId
        var orchestratorInput = new ScrapeOrchestratorInput(
            scrapeRun.Id,
            scrapeRequest?.MaxListingsToFetch,
            scrapeRequest?.LookbackDays);

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(ScrapeOrchestrator), orchestratorInput);

        // Update ScrapeRun with instanceId
        scrapeRun.InstanceId = instanceId;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Started orchestration {InstanceId} for run {RunId}", instanceId, scrapeRun.Id);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new { runId = scrapeRun.Id, instanceId });
        return response;
    }
}

public record StartScrapeRequest(int? MaxListingsToFetch, int? LookbackDays);
