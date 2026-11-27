using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HotstarScraper.Models;
using static HotstarScraper.Services.Json;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Reads a movie or show page's API response:
    ///   HeroGECWidget        title, synopsis, year, age rating, runtime, genres, languages, artwork
    ///   ContentInfoWidget    IMDb rating, directors, producers, creators, cast (not every title has it)
    ///   CategoryTrayWidget   "EpisodeNavigation": season tabs and the open season's first episodes
    /// </summary>
    public static partial class DetailParser
    {
        /// <summary>
        /// Parse a title page. Returns null unless the page is for <paramref name="link"/>:
        /// the content id inside the response must match the id in the link, so a slow or stale
        /// response can never be saved under another title.
        /// </summary>
        public static TitleDetail? ParseTitle(string json, ContentLink link, out string problem)
        {
            problem = string.Empty;
            JsonNode? root = TryParse(json);

            JsonObject? hero = FindByType(root, ".HeroGECWidget").FirstOrDefault();
            JsonNode? info = hero?["data"]?["content_info"];
            if (info is null)
            {
                problem = "page has no title header (HeroGECWidget)";
                return null;
            }

            string pageId = Str(info["content_id"]);
            if (pageId == "") pageId = Str(hero!["data"]?["cw_info"]?["content_id"]);
            if (pageId != link.ContentId)
            {
                problem = $"content id mismatch: asked for {link.ContentId}, page is {(pageId == "" ? "unknown" : pageId)}";
                return null;
            }

            TitleDetail title = link.Type == ContentType.Movie ? new Movie() : new TvShow();
            title.ContentId = pageId;
            title.Url = link.DetailUrl;
            title.Title = Str(info["title"]);
            title.Description = Str(info["description"]);
            title.ImageUrl = Image(hero!["data"]?["hero_img"]?["src"]);
            title.TitleImageUrl = Image(info["title_cutout"]?["src"]);
            title.Callouts = Callouts(info["callout_meta_tags"]);
            // The genre list sometimes repeats a badge such as "Free For You"; badges live in Callouts.
            title.Genres = TagValues(info["enriched_meta_tags"] ?? info["subscript_tags"])
                .Except(title.Callouts, StringComparer.OrdinalIgnoreCase).ToList();

            if (title.Title == "")
            {
                problem = "page has an empty title";
                return null;
            }

            ApplyCoreTags(title, TagValues(info["core_meta_tags"]));

            if (info["content_language_selector"]?["languages"] is JsonArray langs)
            {
                var names = langs.Select(l => Str(l?["language"]?["display_name"])).Where(n => n != "").Distinct().ToList();
                if (names.Count > 0) title.Languages = names;
            }

            JsonObject? contentInfo = FindByType(root, ".ContentInfoWidget").FirstOrDefault();
            if (contentInfo is not null) ApplyContentInfo(title, contentInfo);

            if (title is TvShow show)
            {
                JsonObject? episodeNav = FindByType(root, ".CategoryTrayWidget")
                    .FirstOrDefault(w => Str(w["widget_commons"]?["name"]) == "EpisodeNavigation"
                                      || Str(w["widget_commons"]?["id"]) == "EpisodeNavigation");
                if (episodeNav is not null) ApplySeasons(show, episodeNav);
            }

            return title;
        }

        /// <summary>
        /// Parse a season's episode list response (after clicking a season tab or "View More").
        /// Returns the episodes and the URL of the next page of episodes, if any.
        /// </summary>
        public static (List<Episode> Episodes, string NextPageUrl) ParseEpisodes(string json, Season season)
        {
            JsonNode? root = TryParse(json);
            var episodes = new List<Episode>();
            string next = string.Empty;

            foreach (JsonObject items in FindByType(root, ".CategoryTrayItemsWidget"))
            {
                AddEpisodes(items["data"], season, episodes);
                string n = Str(items["data"]?["next_tray_url"]);
                if (n != "") next = n;
            }

            return (episodes, next);
        }

        /// <summary>Season id carried by an episode-list API URL.</summary>
        public static string SeasonIdFromUrl(string url)
        {
            string id = QueryParam(url, "season_content_id");
            return id != "" ? id : QueryParam(url, "season_id");
        }

        // ---------- pieces ----------

        /// <summary>core_meta_tags look like ["2026", "U/A 16+", "1h 48m", "4 Languages"] or ["2026", "U/A 16+", "1 Season", "English"].</summary>
        private static void ApplyCoreTags(TitleDetail title, List<string> tags)
        {
            foreach (string tag in tags)
            {
                if (title.ReleaseYear is null && YearRegex().IsMatch(tag))
                    title.ReleaseYear = int.Parse(tag, CultureInfo.InvariantCulture);
                else if (title.AgeRating == "" && AgeRatingRegex().IsMatch(tag))
                    title.AgeRating = tag;
                else if (title is Movie movie && movie.RuntimeMinutes is null && ParseRuntime(tag) is int minutes)
                    movie.RuntimeMinutes = minutes;
                else if (!SeasonsTagRegex().IsMatch(tag) && !LanguagesCountRegex().IsMatch(tag) && title.Languages.Count == 0 && tag.All(c => char.IsLetter(c) || c == ' '))
                    title.Languages = new List<string> { tag };
            }
        }

        private static void ApplyContentInfo(TitleDetail title, JsonObject widget)
        {
            if (widget["headerInfo"]?["callout_tags"]?["callout_tag"] is JsonArray callouts)
            {
                foreach (JsonNode? c in callouts)
                {
                    Match m = ImdbRegex().Match(Str(c?["txt"]?["text"]));
                    if (m.Success && decimal.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rating))
                        title.ImdbRating = rating;
                }
            }

            if (widget["content_info_stacks"] is not JsonArray stacks) return;

            foreach (JsonNode? entry in stacks.SelectMany(s => s?["content_info_list"] as JsonArray ?? new JsonArray()))
            {
                string label = Segments(entry?["label"]).ToLowerInvariant();
                List<string> values = SplitPeople(Segments(entry?["value"]));
                if (values.Count == 0) continue;

                if (label.StartsWith("cast") || label.StartsWith("starring") || label.StartsWith("actor")) title.Cast = values;
                else if (label.StartsWith("director")) title.Directors = values;
                else if (label.StartsWith("producer")) title.Producers = values;
                else if (label.StartsWith("creator") || label.StartsWith("created")) title.Creators = values;
                else if (label.StartsWith("language") && title.Languages.Count <= 1) title.Languages = values;
            }
        }

        private static void ApplySeasons(TvShow show, JsonObject episodeNav)
        {
            JsonNode? data = episodeNav["data"];

            if (data?["category_picker"]?["data"]?["tabs"] is JsonArray tabs)
            {
                foreach (JsonNode? t in tabs)
                {
                    JsonNode? tab = t?["tab"]?["data"];
                    string name = Str(tab?["title"]);
                    string trayUrl = Str(tab?["tray_widget_url"]);
                    if (name == "") continue;

                    string seasonId = SeasonIdFromUrl(trayUrl);
                    if (seasonId == "") seasonId = $"{show.ContentId}-{show.Seasons.Count + 1}";

                    Match number = SeasonNameRegex().Match(name);
                    Match eps = EpisodeCountRegex().Match(Str(tab?["sub_title"]));

                    show.Seasons.Add(new Season
                    {
                        SeasonId = seasonId,
                        ShowId = show.ContentId,
                        Position = show.Seasons.Count + 1,
                        Title = name,
                        SeasonNumber = number.Success ? int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                        EpisodeCount = eps.Success ? int.Parse(eps.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                        IsSelected = Bool(tab?["is_selected"])
                    });
                }
            }

            // A show with a single unnamed list of episodes still gets one season row.
            if (show.Seasons.Count == 0)
                show.Seasons.Add(new Season { SeasonId = $"{show.ContentId}-1", ShowId = show.ContentId, Position = 1, Title = "Episodes", IsSelected = true });

            Season open = show.Seasons.FirstOrDefault(s => s.IsSelected) ?? show.Seasons[0];
            open.IsSelected = true;
            AddEpisodes(data?["tray_items"]?["data"], open, open.Episodes);
        }

        private static void AddEpisodes(JsonNode? itemsData, Season season, List<Episode> into)
        {
            if (itemsData?["items"] is not JsonArray items) return;

            foreach (JsonNode? item in items)
            {
                JsonNode? card = item?["playable_content"]?["data"] ?? FirstValue(item)?["data"];
                if (card is null) continue;

                string id = Str(card["content_id"]);
                if (id == "") id = Str(card["cw_info"]?["content_id"]);
                if (id == "") continue;

                var episode = new Episode
                {
                    EpisodeId = id,
                    ShowId = season.ShowId,
                    SeasonId = season.SeasonId,
                    SeasonNumber = season.SeasonNumber,
                    Title = Str(card["title"]),
                    Description = Str(card["description"]),
                    ImageUrl = Image(card["poster"]?["src"]),
                    Url = Link(card["actions"])
                };

                // tags look like ["S19 E1", "24 Aug 2025", "1h 38m"]
                foreach (string tag in TagValues(card["tags"]))
                {
                    Match se = SeasonEpisodeRegex().Match(tag);
                    if (se.Success)
                    {
                        episode.SeasonNumber ??= int.Parse(se.Groups[1].Value, CultureInfo.InvariantCulture);
                        episode.EpisodeNumber = int.Parse(se.Groups[2].Value, CultureInfo.InvariantCulture);
                    }
                    else if (episode.AirDate is null && DateOnly.TryParseExact(tag, new[] { "d MMM yyyy", "dd MMM yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                    {
                        episode.AirDate = date;
                    }
                    else if (episode.RuntimeMinutes is null && ParseRuntime(tag) is int minutes)
                    {
                        episode.RuntimeMinutes = minutes;
                    }
                }

                if (into.All(e => e.EpisodeId != id)) into.Add(episode);
            }
        }

        /// <summary>"1h 48m" → 108, "33m" → 33, "2h" → 120; anything else → null.</summary>
        private static int? ParseRuntime(string text)
        {
            Match m = RuntimeRegex().Match(text);
            if (!m.Success || (!m.Groups[1].Success && !m.Groups[2].Success)) return null;
            int hours = m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            int minutes = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            return hours * 60 + minutes;
        }

        /// <summary>Split "A (Role, Other), B, C" on commas that are not inside brackets.</summary>
        private static List<string> SplitPeople(string text)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && depth > 0) depth--;
                else if (text[i] == ',' && depth == 0)
                {
                    parts.Add(text[start..i]);
                    start = i + 1;
                }
            }
            parts.Add(text[start..]);

            return parts.Select(p => p.Trim()).Where(p => p != "").Distinct().ToList();
        }

        [GeneratedRegex(@"^(19|20)\d{2}$")] private static partial Regex YearRegex();
        [GeneratedRegex(@"^(U|UA|U/A|A|S)(\s?\d+\+)?$|^(U/A|UA)\s?\d+\+$", RegexOptions.IgnoreCase)] private static partial Regex AgeRatingRegex();
        [GeneratedRegex(@"^\s*(?:(\d+)\s*h)?\s*(?:(\d+)\s*m)?\s*$")] private static partial Regex RuntimeRegex();
        [GeneratedRegex(@"^\d+\s+Seasons?$", RegexOptions.IgnoreCase)] private static partial Regex SeasonsTagRegex();
        [GeneratedRegex(@"^\d+\s+Languages?$", RegexOptions.IgnoreCase)] private static partial Regex LanguagesCountRegex();
        [GeneratedRegex(@"IMDB\s*([\d.]+)", RegexOptions.IgnoreCase)] private static partial Regex ImdbRegex();
        [GeneratedRegex(@"^Season\s+(\d+)$", RegexOptions.IgnoreCase)] private static partial Regex SeasonNameRegex();
        [GeneratedRegex(@"(\d+)\s*Eps?", RegexOptions.IgnoreCase)] private static partial Regex EpisodeCountRegex();
        [GeneratedRegex(@"^S(\d+)\s*E(\d+)$", RegexOptions.IgnoreCase)] private static partial Regex SeasonEpisodeRegex();
    }
}
