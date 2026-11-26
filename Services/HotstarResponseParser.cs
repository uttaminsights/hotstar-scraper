using System.Text.Json.Nodes;
using HotstarScraper.Models;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Turns the JSON that the Hotstar web app loads from its own BFF API into
    /// <see cref="Promotion"/> and <see cref="ContentTray"/> objects.
    ///
    /// The page is built from "widgets" identified by an "@type" field:
    ///   MastheadWidget        rotating hero banners (promotions)
    ///   ScrollableTrayWidget  horizontal rows of titles
    ///   CWTrayWidget          "Continue Watching" - personal, deliberately skipped
    /// </summary>
    public class HotstarResponseParser
    {
        private const string SiteBase = "https://www.hotstar.com";
        private const string ImageBase = "https://img10.hotstar.com/image/upload/f_auto/";

        private readonly List<Promotion> _promotions = new();
        private readonly List<ContentTray> _trays = new();
        private readonly HashSet<string> _seenPromotions = new();
        private readonly HashSet<string> _seenTrays = new();

        public IReadOnlyList<Promotion> Promotions => _promotions;
        public IReadOnlyList<ContentTray> Trays => _trays;

        /// <summary>Parse one API response body. Unknown or malformed JSON is ignored.</summary>
        public void Parse(string json, DateTime scrapedAt)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }

            if (root is null) return;

            foreach (JsonObject widget in FindWidgets(root))
            {
                string type = widget["@type"]?.GetValue<string>() ?? string.Empty;

                if (type.EndsWith(".MastheadWidget"))
                    ParseMasthead(widget, scrapedAt);
                else if (type.EndsWith(".ScrollableTrayWidget"))
                    ParseTray(widget);
                // CWTrayWidget (Continue Watching) and ad widgets are intentionally ignored.
            }
        }

        private void ParseMasthead(JsonObject widget, DateTime scrapedAt)
        {
            if (widget["data"]?["items"] is not JsonArray items) return;

            foreach (JsonNode? item in items)
            {
                // Each item wraps its data in a template key such as "gec_masthead".
                JsonNode? data = FirstValue(item)?["data"];
                JsonNode? info = data?["content_info"];
                if (info is null) continue;

                string title = Str(info["title"]);
                string contentId = Str(data?["content_id"]);
                string key = contentId != "" ? contentId : title;
                if (title == "" || !_seenPromotions.Add(key)) continue;

                _promotions.Add(new Promotion
                {
                    Position = _promotions.Count + 1,
                    Title = title,
                    Description = Str(info["description"]),
                    ImageUrl = Image(data?["hero_img"]?["src"]),
                    RedirectUrl = Link(info["actions"]),
                    ContentId = contentId,
                    Tags = Values(info["core_meta_tags"] ?? info["tags"]),
                    Callouts = Callouts(info["callout_meta_tags"]),
                    ScrapedAt = scrapedAt
                });
            }
        }

        private void ParseTray(JsonObject widget)
        {
            JsonNode? data = widget["data"];
            if (data?["items"] is not JsonArray items || items.Count == 0) return;

            string title = TrayTitle(data);
            if (title == "") return;

            var tray = new ContentTray
            {
                Title = title,
                Layout = (FirstKey(items[0]) ?? string.Empty).Replace('_', ' ')
            };

            foreach (JsonNode? item in items)
            {
                JsonNode? card = FirstValue(item)?["data"];
                if (card is null) continue;

                // Richer details live in the hover/expanded variant when it exists
                // (some layouts nest it one level deeper under "data").
                JsonNode? expanded = card["expanded_content_poster"];
                JsonNode? info = expanded?["content_info"] ?? expanded?["data"]?["content_info"] ?? card["content_info"];

                string itemTitle = Str(info?["title"]);
                if (itemTitle == "") itemTitle = Str(card["footer"]?["title"]);
                if (itemTitle == "") itemTitle = Str(card["image"]?["alt"]);
                if (itemTitle == "") itemTitle = Str(card["title"]);
                if (itemTitle == "") continue;

                // Episode cards have no synopsis but carry e.g. "S20 E82 · 26 Nov" in the footer.
                string description = Str(info?["description"]);
                if (description == "") description = Str(card["footer"]?["sub_title"]);

                tray.Items.Add(new ContentItem
                {
                    Position = tray.Items.Count + 1,
                    Title = itemTitle,
                    Description = description,
                    ImageUrl = Image(card["image"]?["src"]),
                    Url = Link(card["actions"]),
                    ContentId = ContentIdFromLink(Link(card["actions"])),
                    Tags = Values(info?["core_meta_tags"] ?? info?["tags"])
                });
            }

            if (tray.Items.Count == 0) return;

            // Pagination can repeat a row; keep the first copy.
            string key = $"{title}|{tray.Items[0].Title}";
            if (!_seenTrays.Add(key)) return;

            tray.Position = _trays.Count + 1;
            _trays.Add(tray);
        }

        // ---------- helpers ----------

        /// <summary>Depth-first search for every object carrying an "@type" of a widget.</summary>
        private static IEnumerable<JsonObject> FindWidgets(JsonNode node)
        {
            var stack = new Stack<JsonNode>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                JsonNode current = stack.Pop();

                if (current is JsonObject obj)
                {
                    if (obj["@type"] is JsonValue t && t.TryGetValue(out string? type) && type.Contains("widget."))
                    {
                        yield return obj;
                        // Masthead items are themselves widgets but are handled by the parent.
                        if (type.EndsWith(".MastheadWidget") || type.EndsWith(".ScrollableTrayWidget")) continue;
                    }

                    foreach (var property in obj)
                        if (property.Value is not null) stack.Push(property.Value);
                }
                else if (current is JsonArray array)
                {
                    for (int i = array.Count - 1; i >= 0; i--)
                        if (array[i] is not null) stack.Push(array[i]!);
                }
            }
        }

        private static string TrayTitle(JsonNode data)
        {
            JsonNode? header = data["header"] ?? data["tray_header"]?["data"]?["header"];

            string title = Str(header?["regular_tray_header"]?["title"]);
            if (title != "") return title;

            JsonNode? segments = header?["surface_tray_header"]?["title_text"]?["segments"];
            if (segments is JsonArray array)
                return string.Concat(array.Select(s => Str(s?["text"]))).Trim();

            return string.Empty;
        }

        private static JsonNode? FirstValue(JsonNode? node) =>
            node is JsonObject obj && obj.Count > 0 ? obj.First().Value : null;

        private static string? FirstKey(JsonNode? node) =>
            node is JsonObject obj && obj.Count > 0 ? obj.First().Key : null;

        private static string Str(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue(out string? s)) return s.Trim();
            return string.Empty;
        }

        private static string Image(JsonNode? src)
        {
            string path = Str(src);
            if (path == "") return string.Empty;
            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ImageBase + path;
        }

        /// <summary>Public page link from an "actions.on_click" list (page_slug of the target page).</summary>
        private static string Link(JsonNode? actions)
        {
            if (actions?["on_click"] is not JsonArray clicks) return string.Empty;

            foreach (JsonNode? click in clicks)
            {
                string slug = Str(click?["open_page_overlay"]?["page_slug"]);
                if (slug == "") slug = Str(click?["page_navigation"]?["page_slug"]);
                if (slug != "") return slug.StartsWith("http") ? slug : SiteBase + slug;
            }

            return string.Empty;
        }

        /// <summary>
        /// Hotstar URLs carry a numeric content id as their last number-only segment, e.g.
        /// /in/movies/supergirl/1271725797 or /in/clip/news18-india/1271387918/watch.
        /// Category pages (/browse/category/drama/926) are not titles, so they get no id.
        /// </summary>
        private static string ContentIdFromLink(string url)
        {
            if (url.Contains("/browse/")) return string.Empty;
            string[] segments = url.Split('?')[0].TrimEnd('/').Split('/');
            // Older shows have short ids (e.g. /shows/yeh-rishta/586), so any number-only segment counts.
            return segments.LastOrDefault(s => s.Length > 0 && s.All(char.IsDigit)) ?? string.Empty;
        }

        private static List<string> Values(JsonNode? tags)
        {
            if (tags is not JsonArray array) return new();
            return array.Select(t => Str(t?["value"])).Where(v => v != "").Distinct().ToList();
        }

        private static List<string> Callouts(JsonNode? callouts)
        {
            if (callouts is not JsonArray array) return new();
            return array.Select(c => Str(c?["callout_tag"]?["txt"]?["text"])).Where(v => v != "").Distinct().ToList();
        }
    }
}
