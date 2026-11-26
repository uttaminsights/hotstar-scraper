using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HotstarScraper.Models;

namespace HotstarScraper.Services
{
    /// <summary>Writes a run's results to JSON and CSV (Excel / Power BI friendly).</summary>
    public class ExportService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // keep "&", "₹", Hindi text readable
        };

        /// <summary>Creates output/&lt;timestamp&gt;/ and returns its path.</summary>
        public string Export(ScrapeResult result, string outputRoot)
        {
            // Folder named in local time so it matches the console log; data timestamps stay UTC.
            string folder = Path.Combine(outputRoot, result.ScrapedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);

            // JSON without a BOM (tools like Python, Node and jq reject it); CSVs keep one for Excel.
            File.WriteAllText(Path.Combine(folder, "hotstar.json"),
                JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));

            WritePromotionsCsv(result, Path.Combine(folder, "promotions.csv"));
            WriteContentCsv(result, Path.Combine(folder, "content.csv"));

            return folder;
        }

        private static void WritePromotionsCsv(ScrapeResult result, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("position,title,description,tags,callouts,content_id,url,image_url,scraped_at");

            foreach (Promotion p in result.Promotions)
            {
                sb.AppendLine(string.Join(",",
                    p.Position,
                    Csv(p.Title),
                    Csv(p.Description),
                    Csv(string.Join(" | ", p.Tags)),
                    Csv(string.Join(" | ", p.Callouts)),
                    Csv(p.ContentId),
                    Csv(p.RedirectUrl),
                    Csv(p.ImageUrl),
                    Csv(p.ScrapedAt.ToString("o"))));
            }

            // UTF-8 with BOM so Excel shows non-English titles correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void WriteContentCsv(ScrapeResult result, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("tray_position,tray_title,tray_layout,item_position,title,description,tags,content_id,url,image_url");

            foreach (ContentTray tray in result.Trays)
            {
                foreach (ContentItem item in tray.Items)
                {
                    sb.AppendLine(string.Join(",",
                        tray.Position,
                        Csv(tray.Title),
                        Csv(tray.Layout),
                        item.Position,
                        Csv(item.Title),
                        Csv(item.Description),
                        Csv(string.Join(" | ", item.Tags)),
                        Csv(item.ContentId),
                        Csv(item.Url),
                        Csv(item.ImageUrl)));
                }
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>RFC 4180 quoting; also neutralises spreadsheet formula injection.</summary>
        private static string Csv(string? value)
        {
            value ??= string.Empty;
            if (value.Length > 0 && "=+-@".Contains(value[0])) value = "'" + value;
            bool quote = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
            value = value.Replace("\"", "\"\"");
            return quote ? $"\"{value}\"" : value;
        }
    }
}
