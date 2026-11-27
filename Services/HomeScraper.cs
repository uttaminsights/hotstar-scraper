using System.Collections.Concurrent;
using System.Diagnostics;
using HotstarScraper.Models;
using Microsoft.Playwright;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Step 1 of a run: collect everything the home page promotes.
    /// Opens the home page, scrolls like a visitor so every row loads, then opens each row's
    /// "View All" page and scrolls it to the end. All data comes from the JSON the pages load
    /// from Hotstar's API, which is far more stable than CSS class names.
    /// </summary>
    public class HomeScraper
    {
        private const string ApiMarker = "/api/internal/bff/";

        private readonly IBrowserContext _context;
        private readonly ScraperOptions _options;

        public HomeScraper(IBrowserContext context, ScraperOptions options)
        {
            _context = context;
            _options = options;
        }

        public async Task<(IReadOnlyList<ContentItem> Heroes, IReadOnlyList<ContentTray> Trays)> ScrapeAsync()
        {
            IPage page = await _context.NewPageAsync();
            try
            {
                var parser = await ScrapeHomeAsync(page);
                Log.Info($"Home page: {parser.Heroes.Count} hero banners, {parser.Trays.Count} rows, {parser.Trays.Sum(t => t.Items.Count)} cards");

                if (_options.ViewAll)
                {
                    var rows = parser.Trays.Where(t => t.ViewAllUrl != "").ToList();
                    Log.Info($"Opening \"View All\" for {rows.Count} rows");

                    foreach (ContentTray tray in rows)
                    {
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            tray.ViewAllItems = await ScrapeViewAllAsync(page, tray.ViewAllUrl);
                            Log.Info($"  {tray.Title}: {tray.ViewAllItems.Count} titles ({watch.Elapsed.TotalSeconds:F1}s)");
                        }
                        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                        {
                            Log.Warn($"  {tray.Title}: View All failed ({ex.Message.Split('\n')[0]}); keeping the {tray.Items.Count} home cards");
                        }

                        await page.WaitForTimeoutAsync(_options.PageDelayMs);
                    }
                }

                return (parser.Heroes, parser.Trays);
            }
            finally
            {
                await page.CloseAsync();
            }
        }

        /// <summary>Turn heroes and rows into one promotion per card, in page order.</summary>
        public static List<Promotion> BuildPromotions(IReadOnlyList<ContentItem> heroes, IReadOnlyList<ContentTray> trays)
        {
            var promotions = new List<Promotion>();

            for (int i = 0; i < heroes.Count; i++)
                promotions.Add(ToPromotion(heroes[i], "hero", "Hero Banner", 0, i + 1, onHome: true));

            foreach (ContentTray tray in trays)
            {
                var home = tray.Items.Where(IsTitleCard).ToList();
                var homeKeys = home.Select(Key).ToHashSet();

                // The "View All" grid is the full, ordered list; mark which of its cards also show on the home page.
                var cards = new List<(ContentItem Item, bool OnHome)>();
                var seen = new HashSet<string>();
                foreach (ContentItem item in tray.ViewAllItems.Where(IsTitleCard))
                    if (seen.Add(Key(item))) cards.Add((item, homeKeys.Contains(Key(item))));
                foreach (ContentItem item in home)
                    if (seen.Add(Key(item))) cards.Add((item, true));

                for (int i = 0; i < cards.Count; i++)
                    promotions.Add(ToPromotion(cards[i].Item, "row", tray.Title, tray.Position, i + 1, cards[i].OnHome));
            }

            return promotions;

            static bool IsTitleCard(ContentItem item) => ContentLink.Parse(item.Url).Type != ContentType.Category;
            static string Key(ContentItem item) => item.Url != "" ? item.Url : item.Title;
        }

        private static Promotion ToPromotion(ContentItem item, string type, string section, int sectionPosition, int position, bool onHome) => new()
        {
            SectionType = type,
            SectionTitle = section,
            SectionPosition = sectionPosition,
            Position = position,
            OnHome = onHome,
            Title = item.Title,
            Description = item.Description,
            Tags = item.Tags,
            Callouts = item.Callouts,
            Url = item.Url,
            ImageUrl = item.ImageUrl
        };

        private async Task<HomeParser> ScrapeHomeAsync(IPage page)
        {
            var responses = new ConcurrentBag<(string Url, string Body)>();
            var pendingReads = new ConcurrentBag<Task>();

            void OnResponse(object? _, IResponse response)
            {
                if (!response.Url.Contains(ApiMarker) || !response.Ok) return;
                if (!(response.Headers.TryGetValue("content-type", out string? type) && type.Contains("json"))) return;
                pendingReads.Add(ReadBodyAsync(response, responses));
            }

            page.Response += OnResponse;
            try
            {
                Log.Info($"Opening {_options.Url}");
                await page.GotoAsync(_options.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                await WaitForQuietNetworkAsync(page);
                Log.Info($"Page loaded: {await page.TitleAsync()}");

                await AutoScrollAsync(page);
                await WaitForQuietNetworkAsync(page);
                await Task.WhenAll(pendingReads);
            }
            finally
            {
                page.Response -= OnResponse;
            }

            Log.Info($"Captured {responses.Count} home-page API responses");
            if (_options.SaveRaw) SaveRaw("home", responses);

            var parser = new HomeParser();
            // Responses arrive in any order; the paginated ones carry an increasing offset, so URL order is page order.
            foreach (var (_, body) in responses.OrderBy(r => r.Url.Contains("offset=") ? 1 : 0).ThenBy(r => OffsetOf(r.Url)))
                parser.ParseHome(body);
            return parser;
        }

        /// <summary>Open a "View All" page and scroll until the grid has no more pages (or the page cap is hit).</summary>
        private async Task<List<ContentItem>> ScrapeViewAllAsync(IPage page, string url)
        {
            string first = await PageData.LoadAsync(page, url);
            var (items, next) = HomeParser.ParseGrid(first);

            // Personalised collections sometimes come back empty when the site is busy; one retry fixes it.
            if (items.Count == 0 && next == "")
            {
                await page.WaitForTimeoutAsync(3_000);
                first = await PageData.LoadAsync(page, url);
                (items, next) = HomeParser.ParseGrid(first);
            }
            var raw = new List<(string, string)> { (url, first) };

            int pages = 1;
            while (next != "" && pages < _options.MaxViewAllPages)
            {
                string endpoint = next.Split('?')[0];
                IResponse? response = null;

                // The grid loads its next page when the visitor scrolls near the bottom.
                for (int attempt = 0; attempt < 3 && response is null; attempt++)
                {
                    try
                    {
                        response = await page.RunAndWaitForResponseAsync(
                            async () =>
                            {
                                await page.Mouse.WheelAsync(0, 3000);
                                await page.EvaluateAsync("window.scrollTo(0, document.body.scrollHeight)");
                            },
                            r => r.Url.Split('?')[0].EndsWith(endpoint) && r.Ok,
                            new PageRunAndWaitForResponseOptions { Timeout = 8_000 });
                    }
                    catch (TimeoutException)
                    {
                        await page.Mouse.WheelAsync(0, -600); // nudge up and retry; lazy loaders fire on re-entry
                    }
                }

                if (response is null)
                {
                    Log.Warn($"    stopped after {pages} pages: the next page did not load");
                    break;
                }

                string body = await response.TextAsync();
                raw.Add((response.Url, body));
                var (more, nextPage) = HomeParser.ParseGrid(body);
                items.AddRange(more);
                next = nextPage;
                pages++;

                await page.WaitForTimeoutAsync(_options.ScrollDelayMs / 2);
            }

            if (next != "" && pages >= _options.MaxViewAllPages)
                Log.Warn($"    stopped at the --max-view-all-pages limit ({pages} pages)");

            if (_options.SaveRaw) SaveRaw("viewall-" + url.TrimEnd('/').Split('/').Last(), raw);

            // A grid page can overlap the previous one; keep the first copy of each card.
            var seen = new HashSet<string>();
            return items.Where(i => seen.Add(i.Url != "" ? i.Url : i.Title)).ToList();
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

        private static int OffsetOf(string url) =>
            int.TryParse(Json.QueryParam(url, "offset"), out int offset) ? offset : 0;

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
                Log.Info($"Scroll {step}/{_options.MaxScrolls}: page height {height}px");

                // Rows load lazily and the site is sometimes slow: before counting a step as "nothing new",
                // let pending requests finish and measure again.
                if (height == previousHeight)
                {
                    await WaitForQuietNetworkAsync(page, 8_000);
                    height = await page.EvaluateAsync<int>("document.body.scrollHeight");
                }

                unchangedSteps = height == previousHeight ? unchangedSteps + 1 : 0;
                if (unchangedSteps >= 2)
                {
                    Log.Info($"Reached the end of the home page after {step} scrolls");
                    return;
                }

                previousHeight = height;
            }

            Log.Info($"Stopped scrolling at the --max-scrolls limit ({_options.MaxScrolls})");
        }

        private static async Task WaitForQuietNetworkAsync(IPage page, int timeoutMs = 15_000)
        {
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = timeoutMs });
            }
            catch (TimeoutException)
            {
                // Streaming sites keep background requests open; carry on with what has loaded.
            }
        }

        private void SaveRaw(string name, IEnumerable<(string Url, string Body)> responses)
        {
            string folder = Path.Combine(_options.RunFolder, "raw", name);
            Directory.CreateDirectory(folder);

            int i = 0;
            foreach (var (url, body) in responses)
            {
                File.WriteAllText(Path.Combine(folder, $"response_{++i:D2}.json"), body);
                File.AppendAllText(Path.Combine(folder, "index.txt"), $"response_{i:D2}.json  {url}{Environment.NewLine}");
            }
        }
    }
}
