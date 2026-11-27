using System.Diagnostics;
using HotstarScraper.Models;
using Microsoft.Playwright;

namespace HotstarScraper.Services
{
    /// <summary>The outcome of scraping one title page.</summary>
    public record DetailOutcome(ContentLink Link, TitleDetail? Title, string Problem, long ElapsedMs, int Attempts, int EpisodePages);

    /// <summary>
    /// Step 2 of a run: open each movie or show page once and read its details.
    ///
    /// Every response is matched to the page it belongs to (see <see cref="PageData"/>) and the
    /// content id inside it must equal the id being scraped, otherwise the title is retried and,
    /// if it still does not match, reported as a failure instead of being saved.
    ///
    /// For shows it clicks each season tab and "View More" like a visitor, reading the episode
    /// lists the page loads.
    /// </summary>
    public class DetailScraper : IAsyncDisposable
    {
        private const int MaxAttempts = 3;
        private const int RecyclePageEvery = 150; // a fresh tab now and then keeps memory flat on long runs

        private readonly IBrowserContext _context;
        private readonly ScraperOptions _options;
        private IPage? _page;
        private int _pagesOpened;

        public DetailScraper(IBrowserContext context, ScraperOptions options)
        {
            _context = context;
            _options = options;
        }

        /// <param name="storedEpisodes">Episodes already in the database per season id, so unchanged seasons are skipped.</param>
        public async Task<DetailOutcome> ScrapeAsync(ContentLink link, IReadOnlyDictionary<string, int> storedEpisodes)
        {
            var watch = Stopwatch.StartNew();
            TitleDetail? title = null;
            string problem = string.Empty;
            int attempt = 0;

            while (title is null && attempt < MaxAttempts)
            {
                attempt++;
                IPage page = await GetPageAsync();

                try
                {
                    string json = await PageData.LoadAsync(page, link.DetailUrl);
                    if (_options.SaveRaw) SaveRaw(link.ContentId, json);
                    title = DetailParser.ParseTitle(json, link, out problem);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                {
                    problem = ex.Message.Split('\n')[0];
                }

                if (title is null && attempt < MaxAttempts)
                {
                    Log.Warn($"  {link.ContentId}: {problem}; retrying");
                    await page.WaitForTimeoutAsync(2_000 * attempt);
                }
            }

            int episodePages = 0;
            if (title is TvShow show && _options.Episodes)
                episodePages = await LoadEpisodesAsync(show, storedEpisodes);

            return new DetailOutcome(link, title, title is null ? problem : string.Empty, watch.ElapsedMilliseconds, attempt, episodePages);
        }

        /// <summary>Fill each season's episodes. Returns how many episode-list pages were loaded.</summary>
        private async Task<int> LoadEpisodesAsync(TvShow show, IReadOnlyDictionary<string, int> storedEpisodes)
        {
            IPage page = _page!;
            int loads = 0;

            // The open tab first (its first page is already loaded), then the others in tab order.
            foreach (Season season in show.Seasons.OrderByDescending(s => s.IsSelected).ThenBy(s => s.Position))
            {
                bool alreadyStored = storedEpisodes.TryGetValue(season.SeasonId, out int stored)
                                     && season.EpisodeCount is int expected && stored >= expected;
                if (alreadyStored && !_options.Full)
                {
                    season.Episodes.Clear(); // nothing new; keep what the database has
                    season.EpisodesUnchanged = true;
                    continue;
                }

                try
                {
                    if (!season.IsSelected)
                    {
                        season.Episodes.Clear();
                        IResponse response = await page.RunAndWaitForResponseAsync(
                            () => Visible(page.GetByText(season.Title, new PageGetByTextOptions { Exact = true }))
                                      .ClickAsync(new LocatorClickOptions { Timeout = 15_000 }),
                            r => r.Ok && DetailParser.SeasonIdFromUrl(r.Url) == season.SeasonId,
                            new PageRunAndWaitForResponseOptions { Timeout = 20_000 });

                        loads++;
                        AddEpisodes(season, await response.TextAsync());
                    }

                    loads += await LoadMoreEpisodesAsync(page, season);
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
                {
                    Log.Warn($"  {show.Title} / {season.Title}: episodes stopped at {season.Episodes.Count} ({ex.Message.Split('\n')[0]})");
                }

                if (season.EpisodeCount is int count && season.Episodes.Count < count)
                    Log.Warn($"  {show.Title} / {season.Title}: {season.Episodes.Count} of {count} episodes collected");
            }

            return loads;
        }

        /// <summary>Click "View More" under the episode list until the season is complete.</summary>
        private async Task<int> LoadMoreEpisodesAsync(IPage page, Season season)
        {
            int loads = 0;

            while ((season.EpisodeCount is null || season.Episodes.Count < season.EpisodeCount) && loads < _options.MaxEpisodePages)
            {
                // Hover cards elsewhere on the page also carry a hidden "View More"; only the visible one is the episode list's.
                ILocator viewMore = Visible(page.GetByText("View More", new PageGetByTextOptions { Exact = true }));
                try
                {
                    await viewMore.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 8_000 });
                }
                catch (TimeoutException)
                {
                    break; // no more pages
                }

                IResponse response = await page.RunAndWaitForResponseAsync(
                    () => viewMore.ClickAsync(),
                    r => r.Ok && r.Url.Contains("/items") && DetailParser.SeasonIdFromUrl(r.Url) == season.SeasonId,
                    new PageRunAndWaitForResponseOptions { Timeout = 15_000 });

                loads++;
                int before = season.Episodes.Count;
                AddEpisodes(season, await response.TextAsync());
                if (season.Episodes.Count == before) break; // the site returned nothing new

                await page.WaitForTimeoutAsync(_options.EpisodeDelayMs);
            }

            if (loads >= _options.MaxEpisodePages)
                Log.Warn($"  {season.Title}: stopped at the --max-episode-pages limit ({loads})");

            return loads;
        }

        private static ILocator Visible(ILocator locator) =>
            locator.Filter(new LocatorFilterOptions { Visible = true }).First;

        private static void AddEpisodes(Season season, string json)
        {
            var (episodes, _) = DetailParser.ParseEpisodes(json, season);
            foreach (Episode e in episodes)
                if (season.Episodes.All(x => x.EpisodeId != e.EpisodeId)) season.Episodes.Add(e);
        }

        private async Task<IPage> GetPageAsync()
        {
            if (_page is not null && _pagesOpened < RecyclePageEvery)
            {
                _pagesOpened++;
                return _page;
            }

            if (_page is not null) await _page.CloseAsync();
            _page = await _context.NewPageAsync();
            _pagesOpened = 1;
            return _page;
        }

        private void SaveRaw(string contentId, string json)
        {
            string folder = Path.Combine(_options.RunFolder, "raw", "titles");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, contentId + ".json"), json);
        }

        public async ValueTask DisposeAsync()
        {
            if (_page is not null) await _page.CloseAsync();
        }
    }
}
