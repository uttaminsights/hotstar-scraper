namespace HotstarScraper.Models
{
    /// <summary>A horizontal row of titles on the home page, e.g. "New on JioHotstar".</summary>
    public class ContentTray
    {
        public int Position { get; set; }

        public string Title { get; set; } = string.Empty;

        /// <summary>Card layout used by the row (vertical poster, horizontal card, ...).</summary>
        public string Layout { get; set; } = string.Empty;

        /// <summary>Link behind the row's "View All" button, if it has one.</summary>
        public string ViewAllUrl { get; set; } = string.Empty;

        /// <summary>Cards shown on the home page.</summary>
        public List<ContentItem> Items { get; set; } = new();

        /// <summary>Every card on the row's "View All" page (empty when not visited).</summary>
        public List<ContentItem> ViewAllItems { get; set; } = new();
    }

    /// <summary>A single card inside a row or a "View All" grid.</summary>
    public class ContentItem
    {
        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public List<string> Tags { get; set; } = new();

        public List<string> Callouts { get; set; } = new();
    }
}
