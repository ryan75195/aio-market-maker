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
            _log.LogDebug("GET {Url}", url);
            using var resp = await _client.GetAsync(url, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var html = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return html;
        }
    }
}
