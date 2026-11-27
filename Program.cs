using System.Diagnostics;
using HotstarScraper.Models;
using HotstarScraper.Services;
using Microsoft.Playwright;
using MySqlConnector;

namespace HotstarScraper
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            ScraperOptions options = ScraperOptions.FromArgs(args);

            DateTime started = DateTime.UtcNow;
            var result = new ScrapeResult { RunId = started.ToString("yyyyMMdd-HHmmss"), SourceUrl = options.Url, ScrapedAt = started };
            options.RunFolder = Path.Combine(options.OutputDirectory, started.ToLocalTime().ToString("yyyyMMdd-HHmmss"));

            Console.WriteLine("Hotstar Scraper");
            Console.WriteLine($"Run {result.RunId} | {(options.Full ? "full" : "incremental")} | " +
                              $"database: {(options.ConnectionString == "" ? "none (files only)" : "MySQL")}");
            Console.WriteLine();

            Database? db = null;
            try
            {
                // Connect first, so a bad connection string fails in seconds, not after a long scrape.
                if (options.ConnectionString != "")
                {
                    db = await Database.OpenAsync(options.ConnectionString);
                    Log.Info("Connected to MySQL; tables are ready");
                }

                using var playwright = await Playwright.CreateAsync();
                await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = options.Headless });
                await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    Locale = "en-IN",
                    ViewportSize = new ViewportSize { Width = 1440, Height = 900 }
                });

                // Step 1: what the home page promotes.
                var homeWatch = Stopwatch.StartNew();
                var (heroes, trays) = await new HomeScraper(context, options).ScrapeAsync();
                result.Promotions = HomeScraper.BuildPromotions(heroes, trays);
                result.HomeDuration = homeWatch.Elapsed;
                Log.Info($"{result.Promotions.Count} promotions collected in {homeWatch.Elapsed.TotalSeconds:F0}s");

                if (result.Promotions.Count == 0)
                {
                    Log.Error("No content found. The site layout may have changed - rerun with --save-raw and inspect the responses.");
                    return 2;
                }

                // Step 2: each promoted movie or show, once.
                if (options.Details)
                    await ScrapeTitlesAsync(context, options, db, result);

                // Step 3: promotions last, so they can link to the titles just saved.
                if (db is not null)
                {
                    await db.SavePromotionsAsync(result.RunId, result.ScrapedAt, result.Promotions);
                    Log.Info($"Saved {result.Promotions.Count} promotions");
                }
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
            {
                Log.Error("Playwright browsers are not installed. Run:  pwsh bin/Debug/net8.0/playwright.ps1 install chromium");
                return 1;
            }
            catch (MySqlException ex)
            {
                Log.Error($"Database error: {ex.Message}");
                return 1;
            }
            catch (Exception ex)
            {
                Log.Error($"Scraping failed: {ex}");
                return 1;
            }
            finally
            {
                // Whatever was collected is still written to files.
                if (result.Promotions.Count > 0)
                {
                    try
                    {
                        new ExportService().Export(result, options.RunFolder);
                        string report = await QualityReport.BuildAsync(result, db);
                        File.WriteAllText(Path.Combine(options.RunFolder, "quality.txt"), report);
                        Console.WriteLine();
                        Console.WriteLine(report);
                        if (db is not null) PrintCounts(await db.CountRowsAsync());
                        Console.WriteLine($"Files saved to {Path.GetFullPath(options.RunFolder)}  (run.json, promotions.csv, quality.txt)");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"Could not write the run files/report: {ex.Message}");
                    }
                }

                if (db is not null) await db.DisposeAsync();
            }

            // Fail the scheduled job loudly if most titles failed (site change or blocking).
            int attempted = result.Movies.Count + result.Shows.Count + result.Failures.Count;
            return attempted > 0 && result.Failures.Count > attempted / 2 ? 3 : 0;
        }

        private static async Task ScrapeTitlesAsync(IBrowserContext context, ScraperOptions options, Database? db, ScrapeResult result)
        {
            var lastScraped = db is null ? new Dictionary<string, DateTime>() : await db.GetLastScrapedAsync();
            var storedEpisodes = db is null ? new Dictionary<string, int>() : await db.GetEpisodeCountsAsync();

            // Episode cards point at their show; every promoted title is visited once, in page order.
            var titles = result.Promotions
                .Select(p => p.Link)
                .Where(l => l.Type is ContentType.Movie or ContentType.Show or ContentType.Episode)
                .Select(l => l.Type == ContentType.Episode ? new ContentLink(ContentType.Show, l.ShowId, l.ShowId, l.DetailUrl) : l)
                .DistinctBy(l => l.ContentId)
                .ToList();

            DateTime staleBefore = DateTime.UtcNow.AddDays(-options.RefreshDays);
            var todo = titles.Where(l => options.Full
                                         || l.Type == ContentType.Show // shows get new episodes; their page is quick
                                         || !lastScraped.TryGetValue(l.ContentId, out DateTime last)
                                         || last < staleBefore)
                             .ToList();
            if (options.MaxTitles > 0) todo = todo.Take(options.MaxTitles).ToList();

            Log.Info($"{titles.Count} unique titles promoted ({titles.Count(t => t.Type == ContentType.Movie)} movies, " +
                     $"{titles.Count(t => t.Type == ContentType.Show)} shows); scraping {todo.Count}");

            var watch = Stopwatch.StartNew();
            await using var scraper = new DetailScraper(context, options);

            for (int i = 0; i < todo.Count; i++)
            {
                ContentLink link = todo[i];
                DetailOutcome outcome = await scraper.ScrapeAsync(link, storedEpisodes);
                result.TitleTimingsMs.Add(outcome.ElapsedMs);

                string kind = link.Type == ContentType.Movie ? "movie" : "show ";
                if (outcome.Title is null)
                {
                    result.Failures.Add(new ScrapeFailure(link.ContentId, link.DetailUrl, outcome.Problem));
                    Log.Warn($"[{i + 1}/{todo.Count}] {kind} {link.ContentId} FAILED after {outcome.Attempts} tries: {outcome.Problem}");
                }
                else
                {
                    if (outcome.Title is Movie movie) result.Movies.Add(movie);
                    if (outcome.Title is TvShow show) result.Shows.Add(show);

                    string extra = outcome.Title is TvShow s
                        ? $" | {s.Seasons.Count} seasons, {s.Seasons.Sum(x => x.Episodes.Count)} episodes loaded"
                        : "";
                    Log.Info($"[{i + 1}/{todo.Count}] {kind} {outcome.Title.Title} ({outcome.ElapsedMs / 1000.0:F1}s){extra}");

                    if (db is not null)
                    {
                        try
                        {
                            await db.SaveTitleAsync(outcome.Title, DateTime.UtcNow);
                        }
                        catch (MySqlException ex)
                        {
                            result.Failures.Add(new ScrapeFailure(link.ContentId, link.DetailUrl, "database: " + ex.Message));
                            Log.Error($"  could not save {outcome.Title.Title}: {ex.Message}");
                        }
                    }
                }

                // Polite pause between title pages.
                if (i < todo.Count - 1) await Task.Delay(options.PageDelayMs);
            }

            result.DetailDuration = watch.Elapsed;
        }

        private static void PrintCounts(Dictionary<string, long> counts)
        {
            Console.WriteLine("Database now holds: " + string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}")));
        }
    }
}
