using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Every page the Hotstar web app opens is described by one POST to /api/internal/bff/v2/start
    /// (or /freshstart for the first page of a session) whose body names the page:
    ///   { "deeplink_url": "/in/movies/supergirl/1271725797", ... }
    /// Matching on that body ties a response to the page we asked for, so data from the previous
    /// page can never be mistaken for the current one.
    /// </summary>
    internal static class PageData
    {
        /// <summary>Open <paramref name="url"/> and return the body of the API response that describes that exact page.</summary>
        public static async Task<string> LoadAsync(IPage page, string url, int timeoutMs = 45_000)
        {
            string path = new Uri(url).AbsolutePath.TrimEnd('/');

            IResponse response = await page.RunAndWaitForResponseAsync(
                () => page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = timeoutMs }),
                r => IsPageDataFor(r, path),
                new PageRunAndWaitForResponseOptions { Timeout = timeoutMs });

            if (!response.Ok) throw new PlaywrightException($"page data request returned HTTP {response.Status}");
            return await response.TextAsync();
        }

        private static bool IsPageDataFor(IResponse response, string path)
        {
            if (response.Request.Method != "POST") return false;

            string endpoint = response.Url.Split('?')[0];
            if (!endpoint.EndsWith("/bff/v2/start") && !endpoint.EndsWith("/bff/v2/freshstart")) return false;

            string? body = response.Request.PostData;
            if (string.IsNullOrEmpty(body)) return false;

            string deeplink;
            try
            {
                deeplink = Json.Str(JsonNode.Parse(body)?["deeplink_url"]);
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }

            return string.Equals(deeplink.Split('?')[0].TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase);
        }
    }
}
