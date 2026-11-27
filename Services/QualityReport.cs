using System.Text;
using HotstarScraper.Models;

namespace HotstarScraper.Services
{
    /// <summary>
    /// Checks a run for the classic scraper bug where one title's data is saved under another
    /// (same synopsis or cast on different ids), plus failed titles, incomplete seasons and
    /// promotions that point at a title that isn't stored. Written to quality.txt.
    /// </summary>
    public static class QualityReport
    {
        public static async Task<string> BuildAsync(ScrapeResult result, Database? db)
        {
            var sb = new StringBuilder();
            var titles = result.Movies.Cast<TitleDetail>().Concat(result.Shows).ToList();

            sb.AppendLine($"Run {result.RunId}  ({result.ScrapedAt:yyyy-MM-dd HH:mm} UTC)");
            sb.AppendLine();

            // 1. Ids must be unique per table.
            Section(sb, "Duplicate content ids in this run (must be empty)",
                titles.GroupBy(t => t.ContentId).Where(g => g.Count() > 1).Select(g => $"{g.Key} x{g.Count()}"));

            // 2. The same synopsis or cast on different ids is how mixed-up data shows itself.
            Section(sb, "Different titles with the same description (check: dubbed versions are fine)",
                titles.Where(t => t.Description.Length > 40)
                      .GroupBy(t => t.Description)
                      .Where(g => g.Count() > 1)
                      .Select(g => string.Join(" | ", g.Select(t => $"{t.Title} [{t.ContentId}]"))));

            Section(sb, "Different titles with the same cast list (check)",
                titles.Where(t => t.Cast.Count >= 3)
                      .GroupBy(t => string.Join(",", t.Cast))
                      .Where(g => g.Count() > 1)
                      .Select(g => string.Join(" | ", g.Select(t => $"{t.Title} [{t.ContentId}]"))));

            var episodeIds = result.Shows.SelectMany(s => s.Seasons).SelectMany(s => s.Episodes)
                                   .GroupBy(e => e.EpisodeId).Where(g => g.Count() > 1).Select(g => g.Key);
            Section(sb, "Episode ids that appear in more than one season (must be empty)", episodeIds);

            // 3. Completeness.
            Section(sb, "Titles that failed",
                result.Failures.Select(f => $"{f.ContentId}  {f.Url}  - {f.Reason}"));

            Section(sb, "Seasons with fewer episodes than the site announces",
                result.Shows.SelectMany(s => s.Seasons.Select(x => (Show: s, Season: x)))
                      .Where(x => !x.Season.EpisodesUnchanged && x.Season.EpisodeCount is int n && x.Season.Episodes.Count < n)
                      .Select(x => $"{x.Show.Title} / {x.Season.Title}: {x.Season.Episodes.Count} of {x.Season.EpisodeCount}"));

            Section(sb, "Titles with no description",
                titles.Where(t => t.Description == "").Select(t => $"{t.Title} [{t.ContentId}]"));

            if (db is not null)
            {
                try
                {
                    Section(sb, "Promotions whose movie/show is not in the database",
                        await db.FindUnlinkedPromotionsAsync(result.RunId));
                    Section(sb, "Whole database: descriptions shared by several titles (check)",
                        await db.FindSharedDescriptionsAsync());
                }
                catch (MySqlConnector.MySqlException ex)
                {
                    sb.AppendLine($"Database checks skipped: {ex.Message}");
                    sb.AppendLine();
                }
            }

            // 4. Timing.
            sb.AppendLine("Timing");
            sb.AppendLine($"  home page + View All : {result.HomeDuration.TotalSeconds:F0}s");
            sb.AppendLine($"  title pages          : {result.DetailDuration.TotalSeconds:F0}s for {result.TitleTimingsMs.Count} titles");
            if (result.TitleTimingsMs.Count > 0)
            {
                var sorted = result.TitleTimingsMs.OrderBy(x => x).ToList();
                sb.AppendLine($"  per title            : median {Pct(sorted, 50) / 1000.0:F1}s, p95 {Pct(sorted, 95) / 1000.0:F1}s, max {sorted[^1] / 1000.0:F1}s");
                sb.AppendLine("  (includes loading episode lists; excludes the pause between titles)");
            }

            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string heading, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            sb.AppendLine($"{heading}: {(list.Count == 0 ? "none" : list.Count.ToString())}");
            foreach (string line in list.Take(50)) sb.AppendLine("  - " + line);
            if (list.Count > 50) sb.AppendLine($"  ... and {list.Count - 50} more");
            sb.AppendLine();
        }

        private static long Pct(List<long> sorted, int p) =>
            sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1)];
    }
}
