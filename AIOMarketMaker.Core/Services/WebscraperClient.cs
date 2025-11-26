// FILE: WebscraperClient.cs
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.Json;
using ScraperWorker.Services;
using Microsoft.Extensions.Logging;       // JobEntity, JobItemEntity

namespace AIOMarketMaker.Api.Services
{
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
        private ILogger<WebscraperClient> _logger;

        public WebscraperClient(HttpClient http, ILogger<WebscraperClient> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _logger = logger;
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
            var resp = await _http.PostAsJsonAsync("api/NewJob", req, ct);
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<StartResponse>(cancellationToken: ct);
            return body ?? throw new InvalidOperationException("Empty response from NewJob");
        }

        /// <summary>
        /// Kicks off a job and waits up to <paramref name="timeout"/> for it to finish,
        /// then returns the HTML of the first page.
        /// </summary>
        public async Task<string> GetPageHtmlAsync(
            string url,
            IEnumerable<object>? proxies = null,
            TimeSpan? timeout = null,
            CancellationToken ct = default)
        {
            var req = new StartRequest([url], null);

            var resp = await _http.PostAsJsonAsync("api/GetPageHtml", req, ct);
            resp.EnsureSuccessStatusCode();

            // API returns raw HTML
            return await resp.Content.ReadAsStringAsync(ct);
        }

        /// <summary>
        /// Polls the status endpoint once and returns the current JobEntity (or null if not found).
        /// </summary>
        public async Task<JobEntity?> GetStatusAsync(string jobId, CancellationToken ct = default)
        {
            var uri = $"api/GetStatus?jobId={Uri.EscapeDataString(jobId)}";
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
            var uri = $"api/GetResults?jobId={Uri.EscapeDataString(jobId)}";
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
