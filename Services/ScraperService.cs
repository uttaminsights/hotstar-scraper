using Microsoft.Playwright;

namespace HotstarScraper.Services
{
    public class ScraperService
    {
        public async Task StartAsync()
        {
            using var playwright = await Playwright.CreateAsync();

            await using var browser =
                await playwright.Chromium.LaunchAsync(
                    new BrowserTypeLaunchOptions
                    {
                        Headless = false
                    });

            var page = await browser.NewPageAsync();

            await page.GotoAsync(
                "https://www.hotstar.com/in",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.NetworkIdle
                });

            Console.WriteLine("Page Loaded");

            await AutoScrollAsync(page);

            Console.WriteLine("Scrolling Completed");

            Console.ReadLine();
        }

        private async Task AutoScrollAsync(IPage page)
        {
            int previousHeight = 0;

            while (true)
            {
                int currentHeight = await page.EvaluateAsync<int>(
                    "document.body.scrollHeight");

                await page.EvaluateAsync(
                    "window.scrollTo(0, document.body.scrollHeight)");

                Console.WriteLine($"Scrolled To: {currentHeight}");

                await page.WaitForTimeoutAsync(3000);

                if (currentHeight == previousHeight)
                {
                    Console.WriteLine("End of page reached");
                    break;
                }

                previousHeight = currentHeight;
            }
        }
    }
}