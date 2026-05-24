using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

            await page.GotoAsync("https://www.hotstar.com/in");

            await page.WaitForTimeoutAsync(5000);

            Console.WriteLine(await page.TitleAsync());
        }
    }
}