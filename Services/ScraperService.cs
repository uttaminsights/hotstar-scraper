using System.Collections.Concurrent;
using HotstarScraper.Models;
using Microsoft.Playwright;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Opens the public home page in a real browser, scrolls like a visitor would,
    /// and captures the JSON the page itself loads from Hotstar's API.
    /// Parsing that JSON is far more stable than scraping CSS class names.
    /// </summary>
    public class ScraperService
    {
        // The web app loads all page content from these endpoints.
        private const string ApiMarker = "/api/internal/bff/";

        private readonly ScraperOptions _options;

        public ScraperService(ScraperOptions options)
        {
            _options = options;
        }

        public async Task<ScrapeResult> StartAsync()
        {
            DateTime scrapedAt = DateTime.UtcNow;
            var responses = new ConcurrentBag<(string Url, string Body)>();
            var pendingReads = new ConcurrentBag<Task>();

            using var playwright = await Playwright.CreateAsync();

            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _options.Headless
            });

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                Locale = "en-IN",
                ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
            });

            IPage page = await context.NewPageAsync();

            page.Response += (_, response) =>
            {
                if (!response.Url.Contains(ApiMarker) || !response.Ok) return;
                if (!(response.Headers.TryGetValue("content-type", out string? type) && type.Contains("json"))) return;

                pendingReads.Add(ReadBodyAsync(response, responses));
            };

            Log($"Opening {_options.Url}");
            await page.GotoAsync(_options.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });

            // Let the first screen (masthead + first rows) finish loading.
            await WaitForQuietNetworkAsync(page);
            Log($"Page loaded: {await page.TitleAsync()}");

            await AutoScrollAsync(page);
            await WaitForQuietNetworkAsync(page);

            await Task.WhenAll(pendingReads);
            Log($"Captured {responses.Count} API responses");

            if (_options.SaveRaw) SaveRawResponses(responses, scrapedAt);

            var parser = new HotstarResponseParser();
            foreach (var (_, body) in responses.OrderBy(r => r.Url))
                parser.Parse(body, scrapedAt);

            return new ScrapeResult
            {
                SourceUrl = _options.Url,
                ScrapedAt = scrapedAt,
                ApiResponsesCaptured = responses.Count,
                Promotions = parser.Promotions.ToList(),
                Trays = parser.Trays.ToList()
            };
        }

        private static async Task ReadBodyAsync(IResponse response, ConcurrentBag<(string, string)> sink)
        {
            try
            {
                sink.Add((response.Url, await response.TextAsync()));
            }
            catch (PlaywrightException)
            {
                // The page navigated or the body was evicted; nothing useful to keep.
            }
        }

        /// <summary>
        /// Scroll until the page stops growing (infinite scroll ended) or the scroll limit is hit.
        /// A fixed pause between steps keeps the request rate low.
        /// </summary>
        private async Task AutoScrollAsync(IPage page)
        {
            int previousHeight = 0;
            int unchangedSteps = 0;

            for (int step = 1; step <= _options.MaxScrolls; step++)
            {
                await page.EvaluateAsync("window.scrollBy(0, window.innerHeight * 1.5)");
                await page.WaitForTimeoutAsync(_options.ScrollDelayMs);

                int height = await page.EvaluateAsync<int>("document.body.scrollHeight");
                Log($"Scroll {step}/{_options.MaxScrolls}: page height {height}px");

                // Rows load lazily, so allow a couple of quiet steps before deciding we reached the end.
                unchangedSteps = height == previousHeight ? unchangedSteps + 1 : 0;
                if (unchangedSteps >= 2)
                {
                    Log("Reached the end of the page");
                    break;
                }

                previousHeight = height;
            }
        }

        private static async Task WaitForQuietNetworkAsync(IPage page)
        {
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15_000 });
            }
            catch (TimeoutException)
            {
                // Streaming sites keep background requests open; carry on with what has loaded.
            }
        }

        private void SaveRawResponses(IEnumerable<(string Url, string Body)> responses, DateTime scrapedAt)
        {
            string folder = Path.Combine(_options.OutputDirectory, scrapedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"), "raw");
            Directory.CreateDirectory(folder);

            int i = 0;
            foreach (var (url, body) in responses.OrderBy(r => r.Url))
            {
                File.WriteAllText(Path.Combine(folder, $"response_{++i:D2}.json"), body);
                File.AppendAllText(Path.Combine(folder, "index.txt"), $"response_{i:D2}.json  {url}{Environment.NewLine}");
            }
        }

        private static void Log(string message) =>
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
