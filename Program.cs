using HotstarScraper.Models;
using HotstarScraper.Services;
using Microsoft.Playwright;

namespace HotstarScraper
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            ScraperOptions options = ScraperOptions.FromArgs(args);

            Console.WriteLine("Hotstar Scraper");
            Console.WriteLine($"Mode: {(options.Headless ? "headless" : "headed")} | max scrolls: {options.MaxScrolls} | delay: {options.ScrollDelayMs} ms");
            Console.WriteLine();

            try
            {
                var scraper = new ScraperService(options);
                ScrapeResult result = await scraper.StartAsync();

                if (result.Promotions.Count == 0 && result.Trays.Count == 0)
                {
                    Console.Error.WriteLine("No content found. The site layout may have changed - rerun with --save-raw and inspect the responses.");
                    return 2;
                }

                string folder = new ExportService().Export(result, options.OutputDirectory);

                Console.WriteLine();
                Console.WriteLine($"Promotions : {result.Promotions.Count}");
                Console.WriteLine($"Rows       : {result.Trays.Count}");
                Console.WriteLine($"Titles     : {result.TotalItems}");
                Console.WriteLine();
                foreach (Promotion p in result.Promotions.Take(5))
                    Console.WriteLine($"  * {p.Title}{(p.Callouts.Count > 0 ? $"  [{string.Join(", ", p.Callouts)}]" : "")}");
                Console.WriteLine();
                Console.WriteLine($"Saved to {Path.GetFullPath(folder)}");
                Console.WriteLine("  hotstar.json, promotions.csv, content.csv");
                return 0;
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist"))
            {
                Console.Error.WriteLine("Playwright browsers are not installed. Run:");
                Console.Error.WriteLine("  pwsh bin/Debug/net8.0/playwright.ps1 install chromium");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Scraping failed: {ex.Message}");
                return 1;
            }
        }
    }
}
