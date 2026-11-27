namespace HotstarScraper.Models
{
    /// <summary>
    /// One title being promoted somewhere on the home page in one run:
    /// a hero banner at the top, or a card in a row (including the extra cards behind "View All").
    /// </summary>
    public class Promotion
    {
        /// <summary>"hero" for the rotating banners, "row" for a content row.</summary>
        public string SectionType { get; set; } = "row";

        /// <summary>"Hero Banner" or the row's title, e.g. "Top 10 in India Today".</summary>
        public string SectionTitle { get; set; } = string.Empty;

        /// <summary>Order of the section on the home page (hero = 0).</summary>
        public int SectionPosition { get; set; }

        /// <summary>1-based position of the title inside its section.</summary>
        public int Position { get; set; }

        /// <summary>True when the card is visible on the home page, false when only reachable through "View All".</summary>
        public bool OnHome { get; set; } = true;

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        /// <summary>Meta tags such as year, age rating and language.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>Highlight badges such as "Free For You" or "#1 in Hindi Today".</summary>
        public List<string> Callouts { get; set; } = new();

        public string Url { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        public ContentLink Link => ContentLink.Parse(Url);
    }
}
