namespace HotstarScraper.Models
{
    /// <summary>Everything collected in one scraping run.</summary>
    public class ScrapeResult
    {
        public string SourceUrl { get; set; } = string.Empty;

        public DateTime ScrapedAt { get; set; }

        public int ApiResponsesCaptured { get; set; }

        public List<Promotion> Promotions { get; set; } = new();

        public List<ContentTray> Trays { get; set; } = new();

        public int TotalItems => Trays.Sum(t => t.Items.Count);
    }
}
