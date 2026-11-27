using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HotstarScraper.Models;

namespace HotstarScraper.Services
{
    /// <summary>Writes a run's results to files: run.json (everything) and promotions.csv (Excel / Power BI).</summary>
    public class ExportService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping // keep "&", "₹", Hindi text readable
        };

        public void Export(ScrapeResult result, string folder)
        {
            Directory.CreateDirectory(folder);

            // JSON without a BOM (tools like Python, Node and jq reject it); CSVs keep one for Excel.
            File.WriteAllText(Path.Combine(folder, "run.json"), JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
            WritePromotionsCsv(result, Path.Combine(folder, "promotions.csv"));
        }

        private static void WritePromotionsCsv(ScrapeResult result, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("section_position,section_title,position,on_home,content_type,content_id,title,description,tags,callouts,url,image_url");

            foreach (Promotion p in result.Promotions)
            {
                ContentLink link = p.Link;
                sb.AppendLine(string.Join(",",
                    p.SectionPosition,
                    Csv(p.SectionTitle),
                    p.Position,
                    p.OnHome ? 1 : 0,
                    link.Type.ToString().ToLowerInvariant(),
                    Csv(link.ContentId),
                    Csv(p.Title),
                    Csv(p.Description),
                    Csv(string.Join(" | ", p.Tags)),
                    Csv(string.Join(" | ", p.Callouts)),
                    Csv(p.Url),
                    Csv(p.ImageUrl)));
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
