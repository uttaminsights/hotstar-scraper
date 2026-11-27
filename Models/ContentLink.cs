namespace HotstarScraper.Models
{
    public enum ContentType
    {
        Other,
        Movie,
        Show,
        Episode,
        /// <summary>A category or collection page (/browse/...), not a title.</summary>
        Category
    }

    /// <summary>
    /// What a Hotstar link points at, worked out from the URL alone:
    ///   /in/movies/supergirl/1271725797                              movie 1271725797
    ///   /in/shows/dig/1271725662                                     show 1271725662
    ///   /in/shows/dig/1271725662/pilot/1271726085/watch              episode 1271726085 of show 1271725662
    ///   /in/sports/cricket/some-highlight/1271727054/watch           other
    ///   /in/browse/editorial/non-stop-sports/6517                    category
    /// </summary>
    public readonly record struct ContentLink(ContentType Type, string ContentId, string ShowId, string DetailUrl)
    {
        private const string SiteBase = "https://www.hotstar.com";

        public static ContentLink Parse(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return default;

            string path = url.Split('?', '#')[0];
            if (path.StartsWith(SiteBase, StringComparison.OrdinalIgnoreCase)) path = path[SiteBase.Length..];
            string[] parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            // parts[0] is the country ("in"), parts[1] the section.
            string section = parts.Length > 1 ? parts[1] : string.Empty;

            if (section == "browse") return new ContentLink(ContentType.Category, string.Empty, string.Empty, string.Empty);

            if (section == "movies" && parts.Length >= 4 && IsId(parts[3]))
                return new ContentLink(ContentType.Movie, parts[3], string.Empty, $"{SiteBase}/{parts[0]}/movies/{parts[2]}/{parts[3]}");

            if (section == "shows" && parts.Length >= 4 && IsId(parts[3]))
            {
                string showUrl = $"{SiteBase}/{parts[0]}/shows/{parts[2]}/{parts[3]}";
                if (parts.Length >= 6 && IsId(parts[5]))
                    return new ContentLink(ContentType.Episode, parts[5], parts[3], showUrl);
                return new ContentLink(ContentType.Show, parts[3], parts[3], showUrl);
            }

            // Sports, clips, live channels...: keep the last number-only segment as the id.
            string id = parts.LastOrDefault(IsId) ?? string.Empty;
            return new ContentLink(ContentType.Other, id, string.Empty, string.Empty);
        }

        private static bool IsId(string s) => s.Length > 0 && s.All(char.IsDigit);
    }
}
