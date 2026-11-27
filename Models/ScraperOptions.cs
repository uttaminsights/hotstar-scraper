namespace HotstarScraper.Models
{
    /// <summary>Run settings, filled from command-line arguments and environment variables.</summary>
    public class ScraperOptions
    {
        public string Url { get; set; } = "https://www.hotstar.com/in";

        /// <summary>Run the browser without a window (default). Use --headed to watch it.</summary>
        public bool Headless { get; set; } = true;

        /// <summary>Upper bound on home-page scroll steps, so a run always finishes.</summary>
        public int MaxScrolls { get; set; } = 15;

        /// <summary>Pause between home-page scroll steps, to stay polite to the site.</summary>
        public int ScrollDelayMs { get; set; } = 2500;

        /// <summary>Pause between page visits (View All pages and title pages).</summary>
        public int PageDelayMs { get; set; } = 1000;

        /// <summary>Pause between "View More" clicks on an episode list.</summary>
        public int EpisodeDelayMs { get; set; } = 500;

        /// <summary>Follow each row's "View All" link to collect the whole row, not just the home-page cards.</summary>
        public bool ViewAll { get; set; } = true;

        public int MaxViewAllPages { get; set; } = 100;

        /// <summary>Visit movie and show pages (step 2). --home-only switches it off.</summary>
        public bool Details { get; set; } = true;

        /// <summary>Collect each show's episodes.</summary>
        public bool Episodes { get; set; } = true;

        /// <summary>Cap on "View More" clicks per season (10 episodes each).</summary>
        public int MaxEpisodePages { get; set; } = 1000;

        /// <summary>
        /// Scrape every promoted title, even ones refreshed recently, and reload every season's episodes.
        /// Without it, a run only visits new titles, shows (to catch new episodes) and movies older than <see cref="RefreshDays"/>.
        /// </summary>
        public bool Full { get; set; }

        public int RefreshDays { get; set; } = 7;

        /// <summary>Only scrape the first N titles (0 = all). For testing.</summary>
        public int MaxTitles { get; set; }

        /// <summary>MySQL connection string; --db or the HOTSTAR_DB environment variable. Empty = files only.</summary>
        public string ConnectionString { get; set; } = Environment.GetEnvironmentVariable("HOTSTAR_DB") ?? string.Empty;

        public string OutputDirectory { get; set; } = "output";

        /// <summary>Also save the raw API JSON responses (useful when the site changes).</summary>
        public bool SaveRaw { get; set; }

        /// <summary>Folder for this run's files; set once the run starts.</summary>
        public string RunFolder { get; set; } = "output";

        public static ScraperOptions FromArgs(string[] args)
        {
            var options = new ScraperOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string? next = i + 1 < args.Length ? args[i + 1] : null;

                switch (arg)
                {
                    case "--headed": options.Headless = false; break;
                    case "--full": options.Full = true; break;
                    case "--home-only": options.Details = false; break;
                    case "--no-view-all": options.ViewAll = false; break;
                    case "--no-episodes": options.Episodes = false; break;
                    case "--save-raw": options.SaveRaw = true; break;
                    case "--max-scrolls" when Int(next, 1, out int v): options.MaxScrolls = v; i++; break;
                    case "--delay" when Int(next, 1000, out int v): options.ScrollDelayMs = v; i++; break;
                    case "--page-delay" when Int(next, 500, out int v): options.PageDelayMs = v; i++; break;
                    case "--max-view-all-pages" when Int(next, 1, out int v): options.MaxViewAllPages = v; i++; break;
                    case "--max-episode-pages" when Int(next, 1, out int v): options.MaxEpisodePages = v; i++; break;
                    case "--refresh-days" when Int(next, 0, out int v): options.RefreshDays = v; i++; break;
                    case "--max-titles" when Int(next, 1, out int v): options.MaxTitles = v; i++; break;
                    case "--db" when !string.IsNullOrWhiteSpace(next): options.ConnectionString = next; i++; break;
                    case "--output" when !string.IsNullOrWhiteSpace(next): options.OutputDirectory = next; i++; break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        Environment.Exit(0);
                        break;
                    default:
                        Console.WriteLine($"Ignoring unknown or invalid argument: {arg}");
                        break;
                }
            }

            return options;

            static bool Int(string? text, int min, out int value) => int.TryParse(text, out value) && value >= min;
        }

        public static void PrintHelp()
        {
            Console.WriteLine("""
                Usage: dotnet run -- [options]

                What to scrape
                  --full                   Re-scrape every promoted title and every season's episodes
                  --refresh-days <n>       Without --full: re-scrape movies older than n days (default 7)
                  --home-only              Only the home page and View All lists, no title pages
                  --no-view-all            Only the cards visible on the home page
                  --no-episodes            Skip episode lists (seasons are still saved)
                  --max-titles <n>         Only scrape the first n titles (testing)

                Where to save
                  --db "<connection>"      MySQL connection string (or set HOTSTAR_DB)
                  --output <dir>           Folder for JSON/CSV/report files (default ./output)
                  --save-raw               Also keep the raw API responses for debugging

                Pace and limits
                  --delay <ms>             Pause between home-page scrolls, min 1000 (default 2500)
                  --page-delay <ms>        Pause between page visits, min 500 (default 1000)
                  --max-scrolls <n>        Home-page scroll steps (default 15)
                  --max-view-all-pages <n> Pages per View All list (default 100)
                  --max-episode-pages <n>  "View More" clicks per season (default 1000)
                  --headed                 Show the browser window
                  -h, --help               Show this help
                """);
        }
    }
}
