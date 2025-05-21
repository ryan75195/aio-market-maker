// FILE: WebscraperClient.cs
using System.Net.Http.Json;
using ScraperWorker.Services;       // JobEntity, JobItemEntity

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
    }

    public class WebscraperClient : IWebscraperClient
    {
        private readonly HttpClient _http;

        public WebscraperClient(HttpClient http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        /// <summary>
        /// Starts a new scrape job and returns the jobId.
        /// </summary>
        public async Task<StartResponse> NewJobAsync(
            IEnumerable<string> urls,
            IEnumerable<object>? proxies = null,
            CancellationToken ct = default)
        {
            var req = new StartRequest(urls, null);
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
            return await resp.Content.ReadFromJsonAsync<JobEntity>(cancellationToken: ct);
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

            var items = await resp.Content.ReadFromJsonAsync<List<JobItemEntity>>(cancellationToken: ct);
            return items ?? new List<JobItemEntity>();
        }
    }
}
