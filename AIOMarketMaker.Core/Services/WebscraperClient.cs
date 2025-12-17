// FILE: WebscraperClient.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using ScraperWorker.Services;
using Microsoft.Extensions.Logging;       // JobEntity, JobItemEntity

namespace AIOMarketMaker.Core.Services
{
    public record ScraperApiConfig(string BaseUrl, string ApiKey);

    public interface IWebscraperClient
    {
        Task<StartResponse> NewJobAsync(
           IEnumerable<string> urls,
           IEnumerable<object>? proxies = null,
           CancellationToken ct = default);

        Task<string> GetPageHtmlAsync(
            string url,
            IEnumerable<object>? proxies = null,
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
        private readonly string _apiKey;

        public WebscraperClient(HttpClient http, ScraperApiConfig config, IJobRepository jobRepository, ILogger<WebscraperClient> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _jobRepository = jobRepository ?? throw new ArgumentNullException(nameof(jobRepository));
            _logger = logger;
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
            CancellationToken ct = default)
        {
            var req = new StartRequest(urls.ToArray(), null);
            var resp = await _http.PostAsJsonAsync(AppendApiKey("api/NewJob"), req, ct);
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
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            // Start a new job for this single URL
            var job = await NewJobAsync(new[] { url }, proxies, ct);
            var jobId = job.JobId;

            // Poll until complete
            var jobStatus = JobStatusType.Pending;
            while (jobStatus != JobStatusType.Success && jobStatus != JobStatusType.Failure)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(2000, ct);

                var currentStatus = await GetStatusAsync(jobId, ct);
                if (currentStatus != null)
                {
                    _logger.LogDebug("Job {JobId} status: {Status}", jobId, currentStatus.Status);
                    jobStatus = currentStatus.Status;
                }
            }

            if (jobStatus == JobStatusType.Failure)
            {
                throw new InvalidOperationException($"Scrape job {jobId} failed");
            }

            // Get results and fetch HTML from blob storage
            var results = await GetResultsAsync(jobId, ct);
            var firstResult = results.FirstOrDefault();

            if (firstResult == null || string.IsNullOrEmpty(firstResult.Url))
            {
                throw new InvalidOperationException($"No results returned for job {jobId}");
            }

            // Fetch HTML from blob storage using the job repository
            var html = await _jobRepository.GetFileContentsAsync(jobId, firstResult.Url, ct);

            if (string.IsNullOrEmpty(html))
            {
                throw new InvalidOperationException($"No HTML content in blob for job {jobId}");
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
    }
}
