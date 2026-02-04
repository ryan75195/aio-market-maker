using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using AIOMarketMaker.Etl.Models;
using AIOMarketMaker.Etl.Services;

[assembly: InternalsVisibleTo("AIOMarketMaker.Tests")]

namespace AIOMarketMaker.Etl.Triggers;

public class ScrapeTrigger
{
    private record ResolveJobsResult(IEnumerable<ScrapeJobConfig> Jobs, HttpResponseData? ErrorResponse);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxWaitTime = TimeSpan.FromHours(2);

    private readonly ILogger<ScrapeTrigger> _logger;
    private readonly IScrapeRunService _scrapeRunService;
    private readonly Func<TimeSpan, Task> _delay;

    public ScrapeTrigger(
        ILogger<ScrapeTrigger> logger,
        IScrapeRunService scrapeRunService)
        : this(logger, scrapeRunService, Task.Delay)
    {
    }

    // Constructor for testing to inject a delay function
    internal ScrapeTrigger(
        ILogger<ScrapeTrigger> logger,
        IScrapeRunService scrapeRunService,
        Func<TimeSpan, Task> delay)
    {
        _logger = logger;
        _scrapeRunService = scrapeRunService;
        _delay = delay;
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

        await ProcessJobsSequentially(jobs, "Nightly");
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

        var runs = await ProcessJobsSequentially(resolved.Jobs, "Manual");

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

    private async Task<IEnumerable<StartedScrapeRun>> ProcessJobsSequentially(
        IEnumerable<ScrapeJobConfig> jobs, string triggerType)
    {
        var completedRuns = new List<StartedScrapeRun>();
        var jobList = jobs.ToList();
        var total = jobList.Count;

        for (var i = 0; i < total; i++)
        {
            var job = jobList[i];
            _logger.LogInformation(
                "Starting job {Current}/{Total}: {SearchTerm}",
                i + 1, total, job.SearchTerm);

            var run = await _scrapeRunService.StartRun(job, triggerType);
            completedRuns.Add(run);

            await WaitForCompletion(run.RunId, job.SearchTerm);

            _logger.LogInformation(
                "Completed job {Current}/{Total}: {SearchTerm}",
                i + 1, total, job.SearchTerm);
        }

        _logger.LogInformation("All {Count} jobs completed", total);
        return completedRuns;
    }

    private async Task WaitForCompletion(int runId, string searchTerm)
    {
        var startTime = DateTime.UtcNow;
        var elapsed = TimeSpan.Zero;

        while (elapsed < MaxWaitTime)
        {
            var isComplete = await _scrapeRunService.IsRunComplete(runId);
            if (isComplete)
            {
                return;
            }

            _logger.LogDebug(
                "Waiting for run {RunId} ({SearchTerm}) to complete. Elapsed: {Elapsed:mm\\:ss}",
                runId, searchTerm, elapsed);

            await _delay(PollInterval);
            elapsed = DateTime.UtcNow - startTime;
        }

        _logger.LogWarning(
            "Run {RunId} ({SearchTerm}) did not complete within {MaxWait} timeout",
            runId, searchTerm, MaxWaitTime);
    }
}
