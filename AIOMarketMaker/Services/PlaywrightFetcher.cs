using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PlaywrightExtraSharp;
using PlaywrightExtraSharp.Plugins.ExtraStealth;
using Microsoft.Playwright;
using PlaywrightExtraSharp.Helpers;
using PlaywrightExtraSharp.Models;

namespace AIOMarketMaker.Services
{
    public sealed class PlaywrightExtraFetcher : IAsyncDisposable, IHtmlFetcher
    {
        private readonly ILogger<PlaywrightExtraFetcher> _log;
        private readonly PlaywrightExtra _px;
        private readonly IBrowser _browser;

        public PlaywrightExtraFetcher(ILogger<PlaywrightExtraFetcher> log)
        {
            _log = log;

            // 1) Build PlaywrightExtra with Chromium + Stealth
            _px = new PlaywrightExtra(BrowserTypeEnum.Chromium)
                      .Install()                    // download if missing
                      .Use(new StealthExtraPlugin());

            // 2) Launch a single browser with persistent context
            _browser = _px
              .LaunchAsync(new BrowserTypeLaunchOptions
              {
                  Headless = true,
                  Args = new[] { "--no-sandbox", "--disable-setuid-sandbox" }
              },
              persistContext: true)
              .GetAwaiter().GetResult();
        }

        public async Task<string> GetStringAsync(string url, CancellationToken token = default)
        {
            // Always create pages via the wrapper
            var page = await _px.NewPageAsync(userDataDir: null /* or your path */);

            try
            {
                _log.LogDebug("PlaywrightExtra GET {Url}", url);

                // Navigate & wait for idle, with cancellation support
                await page.GotoAndWaitForIdleAsync(
                    url,
                    idleTime: TimeSpan.FromMilliseconds(5000)
                );

                return await page.ContentAsync();
            }
            finally
            {
                // Close page (and context, if you didn’t ask for persistence)
                await page.CloseAsync();
                // if you had persistContext=false, you'd also do:
                // await page.Context.CloseAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Only dispose the browser – DO NOT call _px.Dispose()
            await _browser.CloseAsync();
            // NB: no call to _px.Dispose() or CloseAsync() on the wrapper
        }
    }
}
