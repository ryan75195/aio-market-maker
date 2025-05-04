using System.Threading;
using System.Threading.Tasks;

namespace AIOMarketMaker.Services
{
    public sealed class FallbackHtmlFetcher : IHtmlFetcher
    {
        private readonly HtmlFetcher _httpFetcher;
        private readonly PlaywrightExtraFetcher _browserFetcher;

        public FallbackHtmlFetcher(HtmlFetcher httpFetcher,
                                   PlaywrightExtraFetcher browserFetcher)
        {
            _httpFetcher = httpFetcher;
            _browserFetcher = browserFetcher;
        }

        public async Task<string> GetStringAsync(string url,
                                                 CancellationToken token = default)
        {
            var html = await _httpFetcher.GetStringAsync(url, token);
            if (IsBotCheckPage(html))
            {
                html = await _browserFetcher.GetStringAsync(url, token);
            }
            return html;
        }

        private static bool IsBotCheckPage(string html)
        {
            // tweak these markers to whatever your barrier page contains
            return html.Contains("Checking your browser before you access eBay")
                || html.Contains("Reference ID:");
        }
    }
}
