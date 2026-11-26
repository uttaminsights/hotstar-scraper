namespace HotstarScraper.Models
{
    /// <summary>A hero banner from the rotating masthead at the top of the home page.</summary>
    public class Promotion
    {
        public int Position { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        public string RedirectUrl { get; set; } = string.Empty;

        public string ContentId { get; set; } = string.Empty;

        /// <summary>Meta tags such as year, age rating and language.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>Highlight badges such as "Free For You" or "#1 in Hindi Today".</summary>
        public List<string> Callouts { get; set; } = new();

        public DateTime ScrapedAt { get; set; }
    }
}
