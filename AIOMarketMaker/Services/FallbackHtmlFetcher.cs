using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIOMarketMaker.Services
{
    public sealed class FallbackHtmlFetcher : IHtmlFetcher
    {
        private static readonly SemaphoreSlim _throttle = new SemaphoreSlim(25, 25);
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
            // wait (i.e. queue) if there are already 25 in-flight requests
            await _throttle.WaitAsync(token);
            try
            {
                var html = await _httpFetcher.GetStringAsync(url, token);
                if (IsBotCheckPage(html))
                {
                    html = await _browserFetcher.GetStringAsync(url, token);
                }
                return html;
            }
            finally
            {
                _throttle.Release();
            }
        }

        private static bool IsBotCheckPage(string html)
        {
            return html.Contains("Checking your browser before you access eBay")
                || html.Contains("Reference ID:");
        }
    }
}
