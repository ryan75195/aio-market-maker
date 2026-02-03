using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

namespace AIOMarketMaker.Etl.Triggers;

public class ScrapeTrigger
{
    private record ResolveJobsResult(IEnumerable<ScrapeJobConfig> Jobs, HttpResponseData? ErrorResponse);
    private readonly ILogger<ScrapeTrigger> _logger;
    private readonly IScrapeRunService _scrapeRunService;

    public ScrapeTrigger(
        ILogger<ScrapeTrigger> logger,
        IScrapeRunService scrapeRunService)
    {
        _logger = logger;
        _scrapeRunService = scrapeRunService;
    }

    [Function("NightlyScrape")]
    public async Task RunNightly([TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        _logger.LogInformation("Nightly scrape trigger fired");

        var jobs = await _scrapeRunService.GetScrapeJobConfigs();
        if (!jobs.Any())
        {
            _logger.LogInformation("No enabled jobs found for nightly scrape");
            return;
        }

        var runs = await _scrapeRunService.StartRuns(jobs, "Nightly");
        _logger.LogInformation("Started {Count} scrape runs for nightly scrape", runs.Count());
    }

    [Function("ManualScrape")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scrape/start")] HttpRequestData req)
    {
        _logger.LogInformation("Manual scrape trigger fired");

        var scrapeRequest = await ParseRequest(req);

        var resolved = await ResolveJobs(req, scrapeRequest?.JobId);
        if (resolved.ErrorResponse != null)
            return resolved.ErrorResponse;

        var runs = await _scrapeRunService.StartRuns(resolved.Jobs, "Manual");

        return await BuildResponse(req, runs);
    }

    private async Task<ManualScrapeRequest?> ParseRequest(HttpRequestData req)
    {
        var body = await req.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ManualScrapeRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse request body, using defaults");
            return null;
        }
    }

    private async Task<ResolveJobsResult> ResolveJobs(HttpRequestData req, int? jobId)
    {
        if (jobId != null)
        {
            var job = await _scrapeRunService.GetScrapeJobConfig(jobId.Value);
            if (job == null)
            {
                var response = req.CreateResponse(HttpStatusCode.NotFound);
                await response.WriteAsJsonAsync(new ErrorResponse($"Job {jobId} not found"));
                return new ResolveJobsResult(Array.Empty<ScrapeJobConfig>(), response);
            }

            return new ResolveJobsResult(new[] { job }, null);
        }

        var enabledJobs = await _scrapeRunService.GetScrapeJobConfigs();
        if (!enabledJobs.Any())
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new EmptyJobsResponse("No enabled jobs", Array.Empty<ScrapeRunResult>()));
            return new ResolveJobsResult(Array.Empty<ScrapeJobConfig>(), response);
        }

        return new ResolveJobsResult(enabledJobs, null);
    }

    private static async Task<HttpResponseData> BuildResponse(
        HttpRequestData req, IEnumerable<StartedScrapeRun> runs)
    {
        var first = runs.FirstOrDefault();
        var results = runs.Select(r => new ScrapeRunResult(r.JobId, r.RunId, r.Status));
        var body = new ManualScrapeResponse(
            first?.InstanceId ?? Guid.NewGuid().ToString(),
            first?.RunId ?? 0,
            results);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(body);
        return response;
    }
}
