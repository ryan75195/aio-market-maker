using Microsoft.Extensions.Logging;

namespace AIOMarketMaker.Services
{
    public interface IHtmlFetcher
    {
        Task<string> GetStringAsync(string url, CancellationToken token = default);
    }

    public sealed class HtmlFetcher : IHtmlFetcher
    {
        private readonly HttpClient _client;
        private readonly ILogger<HtmlFetcher> _log;

        public HtmlFetcher(HttpClient client, ILogger<HtmlFetcher> log)
        {
            _client = client;
            _log = log;
        }

        public async Task<string> GetStringAsync(string url, CancellationToken token = default)
        {
            // jitter so you don't fetch at perfectly uniform intervals
            await Task.Delay(Random.Shared.Next(500, 1500), token).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            // mimic a real browser
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/114.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd(
                "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

            // if you’re following links, set a referer:
            // request.Headers.Referrer = new Uri("https://example.com/previous-page");

            _log.LogDebug("GET {Url}", url);
            using var resp = await _client.SendAsync(request, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // HttpClientHandler will automatically decompress gzip/deflate/br if configured
            return await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
    }
}