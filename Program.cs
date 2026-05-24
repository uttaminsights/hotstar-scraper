using HotstarScraper.Services;

namespace HotstarScraper
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Hotstar Scraper...");

            ScraperService scraperService = new ScraperService();

            await scraperService.StartAsync();

            Console.WriteLine("Scraping Completed.");
        }
    }
}