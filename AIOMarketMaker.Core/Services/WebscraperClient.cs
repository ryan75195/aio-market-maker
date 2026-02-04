// FILE: WebscraperClient.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using ScraperWorker.Services;
using Microsoft.Extensions.Logging;       // JobEntity, JobItemEntity

namespace AIOMarketMaker.Core.Services
{
    public record ScrapeWorkItem(string ListingId, string DescriptionUrl);

    public record ScraperApiConfig(string BaseUrl, string ApiKey);

    public interface IWebscraperClient
    {
        Task EnqueueScrapeWork(
            IEnumerable<ScrapeWorkItem> items,
            int scrapeRunId,
            int scrapeJobId,
            CancellationToken ct = default);

        Task<StartResponse> NewJobAsync(
           IEnumerable<string> urls,
           IEnumerable<object>? proxies = null,
           string? correlationId = null,
           string? groupId = null,
           string? fileKey = null,
           int? scrapeRunId = null,
           CancellationToken ct = default);

        Task<string> GetPageHtmlAsync(
            string url,
            IEnumerable<object>? proxies = null,
            string? correlationId = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default);

        Task<JobEntity?> GetStatusAsync(string jobId, CancellationToken ct = default);

        Task<IReadOnlyList<JobItemEntity>> GetResultsAsync(
            string jobId,
            CancellationToken ct = default);

        Task<IEnumerable<JobItemEntity>> RunJobAsync(IEnumerable<string> urls);
    }

    public class WebscraperClient : IWebscraperClient
    {
        private readonly HttpClient _http;
        private readonly ILogger<WebscraperClient> _logger;
        private readonly IJobRepository _jobRepository;
        private readonly IQueueService? _queueService;
        private readonly string _apiKey;

        public WebscraperClient(
            HttpClient http,
            ScraperApiConfig config,
            IJobRepository jobRepository,
            ILogger<WebscraperClient> logger,
            IQueueService? queueService = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
            _logger = logger;
            _queueService = queueService;
            _apiKey = config?.ApiKey ?? "";
        }

        private string AppendApiKey(string uri)
        {
            if (string.IsNullOrEmpty(_apiKey)) return uri;
            var separator = uri.Contains("?") ? "&" : "?";
            return $"{uri}{separator}code={Uri.EscapeDataString(_apiKey)}";
        }

        public async Task<IEnumerable<JobItemEntity>> RunJobAsync(IEnumerable<string> urls)
        {
            var job = await this.NewJobAsync(urls);
            var jobId = job.JobId;

            var jobStatus = JobStatusType.Pending;
            while (jobStatus != JobStatusType.Success && jobStatus != JobStatusType.Failure)
            {
                try
                {
                    var currentStatus = await this.GetStatusAsync(jobId);
                    _logger.LogInformation(currentStatus?.ToLogString());
                    jobStatus = currentStatus != null ? currentStatus.Status : jobStatus;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to get and set status.");
                }
                await Task.Delay(5000);
            }

            return await this.GetResultsAsync(jobId);
        }

        /// <summary>
        /// Starts a new scrape job and returns the jobId.
        /// </summary>
        public async Task<StartResponse> NewJobAsync(
            IEnumerable<string> urls,
            IEnumerable<object>? proxies = null,
            string? correlationId = null,
            string? groupId = null,
            string? fileKey = null,
            int? scrapeRunId = null,
            CancellationToken ct = default)
        {
            var req = new StartRequest(urls.ToArray(), null, groupId, fileKey, scrapeRunId);

            var request = new HttpRequestMessage(HttpMethod.Post, AppendApiKey("api/NewJob"))
            {
                Content = JsonContent.Create(req)
            };

            if (!string.IsNullOrEmpty(correlationId))
            {
                request.Headers.Add("X-Correlation-Id", correlationId);
            }

            var resp = await _http.SendAsync(request, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<StartResponse>(cancellationToken: ct);
            return body ?? throw new InvalidOperationException("Empty response from NewJob");
        }

        /// <summary>
        /// Kicks off a job and waits for it to finish, then returns the HTML of the first page.
        /// </summary>
        public async Task<string> GetPageHtmlAsync(
            string url,
            IEnumerable<object>? proxies = null,
            string? correlationId = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;
            var job = await NewJobAsync(new[] { url }, proxies, correlationId, ct: ct);

            // Poll until complete (every 2s, but only log every 60s)
            var jobStatus = JobStatusType.Pending;
            var lastLogTime = startTime;
            while (jobStatus != JobStatusType.Success && jobStatus != JobStatusType.Failure)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(2000, ct);

                var currentStatus = await GetStatusAsync(job.JobId, ct);
                if (currentStatus != null)
                {
                    jobStatus = currentStatus.Status;

                    // Log every 60 seconds if still running
                    if ((DateTime.UtcNow - lastLogTime).TotalSeconds >= 60)
                    {
                        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                        _logger.LogInformation("    Still waiting... ({Elapsed:F0}s elapsed)", elapsed);
                        lastLogTime = DateTime.UtcNow;
                    }
                }
            }

            var totalTime = (DateTime.UtcNow - startTime).TotalSeconds;

            if (jobStatus == JobStatusType.Failure)
            {
                _logger.LogError("    Failed after {Elapsed:F0}s", totalTime);
                throw new InvalidOperationException($"Scrape job {job.JobId} failed");
            }

            _logger.LogInformation("    Done ({Elapsed:F0}s)", totalTime);

            // Get results and fetch HTML from blob storage
            var results = await GetResultsAsync(job.JobId, ct);
            var firstResult = results.FirstOrDefault();

            if (firstResult == null || string.IsNullOrEmpty(firstResult.Url))
            {
                throw new InvalidOperationException($"No results returned for job {job.JobId}");
            }

            // Fetch HTML from blob storage using the job repository
            var html = await _jobRepository.GetFileContentsAsync(job.JobId, firstResult.Url, ct);

            if (string.IsNullOrEmpty(html))
            {
                throw new InvalidOperationException($"No HTML content in blob for job {job.JobId}");
            }

            return html;
        }

        /// <summary>
        /// Polls the status endpoint once and returns the current JobEntity (or null if not found).
        /// </summary>
        public async Task<JobEntity?> GetStatusAsync(string jobId, CancellationToken ct = default)
        {
            var uri = AppendApiKey($"api/GetStatus?jobId={Uri.EscapeDataString(jobId)}");
            var resp = await _http.GetAsync(uri, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            resp.EnsureSuccessStatusCode();

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            var wrapper = await resp.Content
                .ReadFromJsonAsync<JobStatus>(opts, ct);

            return wrapper?.Job;
        }

        /// <summary>
        /// Fetches all JobItemEntity results for a completed job.
        /// </summary>
        public async Task<IReadOnlyList<JobItemEntity>> GetResultsAsync(
            string jobId,
            CancellationToken ct = default)
        {
            var uri = AppendApiKey($"api/GetResults?jobId={Uri.EscapeDataString(jobId)}");
            var resp = await _http.GetAsync(uri, ct);
            resp.EnsureSuccessStatusCode();

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            var wrapper = await resp.Content
                .ReadFromJsonAsync<List<JobItemEntity>>(opts, ct);

            return wrapper ?? new List<JobItemEntity>();
        }

        public async Task EnqueueScrapeWork(
            IEnumerable<ScrapeWorkItem> items,
            int scrapeRunId,
            int scrapeJobId,
            CancellationToken ct = default)
        {
            if (_queueService == null)
                throw new InvalidOperationException("QueueService not configured. Cannot enqueue scrape work.");

            var messages = new List<ScrapeQueueMessage>();
            foreach (var item in items)
            {
                messages.Add(new ScrapeQueueMessage
                {
                    JobId = Guid.NewGuid().ToString("N"),
                    Url = item.DescriptionUrl,
                    GroupId = item.ListingId,
                    FileKey = "description",
                    ScrapeRunId = scrapeRunId,
                    ScrapeJobId = scrapeJobId,
                    EnqueuedAt = DateTimeOffset.UtcNow
                });
            }

            await _queueService.EnqueueBatchAsync(messages, ct);

            _logger.LogInformation("Enqueued {Count} description scrape messages for {ItemCount} listings",
                messages.Count, messages.Count);
        }
    }
}
