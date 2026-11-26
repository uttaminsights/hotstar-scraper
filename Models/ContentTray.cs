namespace HotstarScraper.Models
{
    /// <summary>A horizontal row of titles on the home page, e.g. "New on JioHotstar".</summary>
    public class ContentTray
    {
        public int Position { get; set; }

        public string Title { get; set; } = string.Empty;

        /// <summary>Card layout used by the row (vertical poster, horizontal card, ...).</summary>
        public string Layout { get; set; } = string.Empty;

        public List<ContentItem> Items { get; set; } = new();
    }

    /// <summary>A single title inside a tray.</summary>
    public class ContentItem
    {
        public int Position { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string ContentId { get; set; } = string.Empty;

        public List<string> Tags { get; set; } = new();
    }
}
