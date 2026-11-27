using System.Text.Json.Nodes;
using HotstarScraper.Models;
using static HotstarScraper.Services.Json;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Turns the home page's API responses into hero banners and content rows,
    /// and a "View All" page's responses into its full list of cards.
    ///
    /// The page is built from widgets identified by "@type":
    ///   MastheadWidget        rotating hero banners at the top
    ///   ScrollableTrayWidget  horizontal rows of titles
    ///   GridWidget            the grid on a "View All" page
    ///   CWTrayWidget          "Continue Watching" - personal, deliberately skipped
    /// </summary>
    public class HomeParser
    {
        private readonly List<ContentItem> _heroes = new();
        private readonly List<ContentTray> _trays = new();
        private readonly HashSet<string> _seenHeroes = new();
        private readonly HashSet<string> _seenTrays = new();

        public IReadOnlyList<ContentItem> Heroes => _heroes;
        public IReadOnlyList<ContentTray> Trays => _trays;

        /// <summary>Parse one home-page API response. Unknown or malformed JSON is ignored.</summary>
        public void ParseHome(string json)
        {
            JsonNode? root = TryParse(json);
            if (root is null) return;

            var widgets = FindObjects(root, o =>
            {
                string type = Str(o["@type"]);
                return type.EndsWith(".MastheadWidget") || type.EndsWith(".ScrollableTrayWidget");
            });

            foreach (JsonObject widget in widgets)
            {
                if (Str(widget["@type"]).EndsWith(".MastheadWidget")) ParseMasthead(widget);
                else ParseTray(widget);
            }
        }

        /// <summary>
        /// Parse one response from a "View All" page (the first page load or a later "more items" page).
        /// Returns the cards found and the URL of the next page of the grid, if any.
        /// </summary>
        public static (List<ContentItem> Items, string NextPageUrl) ParseGrid(string json)
        {
            var items = new List<ContentItem>();
            string next = string.Empty;

            foreach (JsonObject grid in FindByType(TryParse(json), ".GridWidget"))
            {
                if (grid["data"]?["items"] is JsonArray array)
                    items.AddRange(array.Select(ParseCard).OfType<ContentItem>());

                string more = Str(grid["data"]?["more_grid_items_url"]);
                if (more != "") next = more;
            }

            return (items, next);
        }

        private void ParseMasthead(JsonObject widget)
        {
            if (widget["data"]?["items"] is not JsonArray items) return;

            foreach (JsonNode? item in items)
            {
                // Each item wraps its data in a template key such as "gec_masthead".
                JsonNode? data = FirstValue(item)?["data"];
                JsonNode? info = data?["content_info"];
                if (info is null) continue;

                string title = Str(info["title"]);
                string url = Link(info["actions"]);
                string key = url != "" ? url : title;
                if (title == "" || !_seenHeroes.Add(key)) continue;

                _heroes.Add(new ContentItem
                {
                    Title = title,
                    Description = Str(info["description"]),
                    ImageUrl = Image(data?["hero_img"]?["src"]),
                    Url = url,
                    Tags = TagValues(info["core_meta_tags"] ?? info["tags"]),
                    Callouts = Callouts(info["callout_meta_tags"])
                });
            }
        }

        private void ParseTray(JsonObject widget)
        {
            JsonNode? data = widget["data"];
            if (data?["items"] is not JsonArray items || items.Count == 0) return;

            JsonNode? header = data["header"] ?? data["tray_header"]?["data"]?["header"];
            string title = TrayTitle(header);
            if (title == "") return;

            var tray = new ContentTray
            {
                Title = title,
                Layout = (FirstKey(items[0]) ?? string.Empty).Replace('_', ' '),
                ViewAllUrl = Link(header?["regular_tray_header"]?["cta"]?["actions"])
            };
            tray.Items.AddRange(items.Select(ParseCard).OfType<ContentItem>());

            if (tray.Items.Count == 0) return;

            // Pagination can repeat a row; keep the first copy.
            if (!_seenTrays.Add($"{title}|{tray.Items[0].Url}|{tray.Items[0].Title}")) return;

            tray.Position = _trays.Count + 1;
            _trays.Add(tray);
        }

        /// <summary>One card in a row or grid. Every layout wraps its data as { "&lt;layout&gt;": { data: ... } }.</summary>
        private static ContentItem? ParseCard(JsonNode? item)
        {
            JsonNode? card = FirstValue(item)?["data"];
            if (card is null) return null;

            // Richer details live in the hover/expanded variant when it exists
            // (some layouts nest it one level deeper under "data").
            JsonNode? expanded = card["expanded_content_poster"];
            JsonNode? info = expanded?["content_info"] ?? expanded?["data"]?["content_info"] ?? card["content_info"];

            string title = Str(info?["title"]);
            if (title == "") title = Str(card["footer"]?["title"]);
            if (title == "") title = Str(card["image"]?["alt"]);
            if (title == "") title = Str(card["title"]);
            if (title == "") return null;

            // Episode cards have no synopsis but carry e.g. "S20 E82 · 26 Nov" in the footer.
            string description = Str(info?["description"]);
            if (description == "") description = Str(card["footer"]?["sub_title"]);

            return new ContentItem
            {
                Title = title,
                Description = description,
                ImageUrl = Image(card["image"]?["src"]),
                Url = Link(card["actions"]),
                Tags = TagValues(info?["core_meta_tags"] ?? info?["tags"]),
                Callouts = Callouts(info?["callout_meta_tags"])
            };
        }

        private static string TrayTitle(JsonNode? header)
        {
            string title = Str(header?["regular_tray_header"]?["title"]);
            if (title != "") return title;

            return Segments(header?["surface_tray_header"]?["title_text"]);
        }
    }
}
