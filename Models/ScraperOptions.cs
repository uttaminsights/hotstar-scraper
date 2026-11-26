namespace HotstarScraper.Models
{
    /// <summary>Run settings, filled from command-line arguments.</summary>
    public class ScraperOptions
    {
        public string Url { get; set; } = "https://www.hotstar.com/in";

        /// <summary>Run the browser without a window (default). Use --headed to watch it.</summary>
        public bool Headless { get; set; } = true;

        /// <summary>Upper bound on scroll steps, so a run always finishes.</summary>
        public int MaxScrolls { get; set; } = 15;

        /// <summary>Pause between scroll steps, to stay polite to the site.</summary>
        public int ScrollDelayMs { get; set; } = 2500;

        public string OutputDirectory { get; set; } = "output";

        /// <summary>Also save the raw API JSON responses (useful when the site changes).</summary>
        public bool SaveRaw { get; set; }

        public static ScraperOptions FromArgs(string[] args)
        {
            var options = new ScraperOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string? next = i + 1 < args.Length ? args[i + 1] : null;

                switch (arg)
                {
                    case "--headed":
                        options.Headless = false;
                        break;
                    case "--max-scrolls" when int.TryParse(next, out int scrolls) && scrolls > 0:
                        options.MaxScrolls = scrolls;
                        i++;
                        break;
                    case "--delay" when int.TryParse(next, out int delay) && delay >= 1000:
                        options.ScrollDelayMs = delay;
                        i++;
                        break;
                    case "--output" when !string.IsNullOrWhiteSpace(next):
                        options.OutputDirectory = next;
                        i++;
                        break;
                    case "--save-raw":
                        options.SaveRaw = true;
                        break;
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
        }

        public static void PrintHelp()
        {
            Console.WriteLine("""
                Usage: dotnet run -- [options]

                  --headed            Show the browser window while scraping
                  --max-scrolls <n>   Maximum scroll steps (default 15)
                  --delay <ms>        Pause between scrolls, min 1000 (default 2500)
                  --output <dir>      Output folder (default ./output)
                  --save-raw          Also save the raw API responses
                  -h, --help          Show this help
                """);
        }
    }
}
