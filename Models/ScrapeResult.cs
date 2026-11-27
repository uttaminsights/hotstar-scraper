namespace HotstarScraper.Models
{
    /// <summary>Everything collected in one scraping run.</summary>
    public class ScrapeResult
    {
        /// <summary>yyyyMMdd-HHmmss in UTC; also stored on every promotion row.</summary>
        public string RunId { get; set; } = string.Empty;

        public string SourceUrl { get; set; } = string.Empty;

        public DateTime ScrapedAt { get; set; }

        public List<Promotion> Promotions { get; set; } = new();

        public List<Movie> Movies { get; set; } = new();

        public List<TvShow> Shows { get; set; } = new();

        /// <summary>Titles that could not be scraped, with the reason.</summary>
        public List<ScrapeFailure> Failures { get; set; } = new();

        /// <summary>Time taken per title page, in milliseconds, in visit order.</summary>
        public List<long> TitleTimingsMs { get; set; } = new();

        public TimeSpan HomeDuration { get; set; }

        public TimeSpan DetailDuration { get; set; }
    }

    public record ScrapeFailure(string ContentId, string Url, string Reason);
}
