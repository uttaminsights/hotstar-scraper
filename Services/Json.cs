using System.Text.Json.Nodes;

namespace HotstarScraper.Services
{
    /// <summary>Small helpers for walking the widget JSON that Hotstar's web app loads.</summary>
    internal static class Json
    {
        public const string SiteBase = "https://www.hotstar.com";
        public const string ImageBase = "https://img10.hotstar.com/image/upload/f_auto/";

        public static JsonNode? TryParse(string json)
        {
            try
            {
                return JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Depth-first search for every object whose "@type" ends with <paramref name="typeSuffix"/>.</summary>
        public static IEnumerable<JsonObject> FindByType(JsonNode? node, string typeSuffix) =>
            FindObjects(node, o => Str(o["@type"]).EndsWith(typeSuffix, StringComparison.Ordinal));

        /// <summary>Depth-first search, pre-order, so results come in document order.</summary>
        public static IEnumerable<JsonObject> FindObjects(JsonNode? node, Func<JsonObject, bool> match, bool descendIntoMatches = false)
        {
            if (node is null) yield break;

            var stack = new Stack<JsonNode>();
            stack.Push(node);

            while (stack.Count > 0)
            {
                JsonNode current = stack.Pop();

                if (current is JsonObject obj)
                {
                    if (match(obj))
                    {
                        yield return obj;
                        if (!descendIntoMatches) continue;
                    }

                    foreach (var property in obj.Reverse())
                        if (property.Value is not null) stack.Push(property.Value);
                }
                else if (current is JsonArray array)
                {
                    for (int i = array.Count - 1; i >= 0; i--)
                        if (array[i] is not null) stack.Push(array[i]!);
                }
            }
        }

        public static string Str(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue(out string? s)) return s.Trim();
            return string.Empty;
        }

        public static bool Bool(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue(out bool b) && b;

        public static JsonNode? FirstValue(JsonNode? node) =>
            node is JsonObject obj && obj.Count > 0 ? obj.First().Value : null;

        public static string? FirstKey(JsonNode? node) =>
            node is JsonObject obj && obj.Count > 0 ? obj.First().Key : null;

        /// <summary>Text of a "segments" rich-text object: { segments: [ { text } ] }.</summary>
        public static string Segments(JsonNode? node)
        {
            if (node?["segments"] is not JsonArray array) return Str(node);
            return string.Concat(array.Select(s => Str(s?["text"]))).Trim();
        }

        public static string Image(JsonNode? src)
        {
            string path = Str(src);
            if (path == "") return string.Empty;
            return path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ImageBase + path;
        }

        /// <summary>Public page link (page_slug) from an "actions.on_click" list.</summary>
        public static string Link(JsonNode? actions)
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

        /// <summary>The "value" of each tag in a tag list, e.g. core_meta_tags.</summary>
        public static List<string> TagValues(JsonNode? tags)
        {
            if (tags is not JsonArray array) return new();
            return array.Select(t => Str(t?["value"])).Where(v => v != "").Distinct().ToList();
        }

        /// <summary>Badge texts such as "Free For You" or "#1 in Hindi Today".</summary>
        public static List<string> Callouts(JsonNode? callouts)
        {
            if (callouts is not JsonArray array) return new();
            return array.Select(c => Str(c?["callout_tag"]?["txt"]?["text"]))
                        .Where(v => v != "").Distinct().ToList();
        }

        /// <summary>Value of one query-string parameter in a relative or absolute URL.</summary>
        public static string QueryParam(string url, string name)
        {
            int q = url.IndexOf('?');
            if (q < 0) return string.Empty;

            foreach (string pair in url[(q + 1)..].Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == name) return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }

            return string.Empty;
        }
    }
}
