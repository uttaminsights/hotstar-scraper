namespace HotstarScraper.Models
{
    /// <summary>Details shared by movies and shows, read from the title's own page.</summary>
    public abstract class TitleDetail
    {
        public string ContentId { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public int? ReleaseYear { get; set; }

        /// <summary>e.g. "U/A 16+".</summary>
        public string AgeRating { get; set; } = string.Empty;

        public decimal? ImdbRating { get; set; }

        public List<string> Genres { get; set; } = new();

        public List<string> Languages { get; set; } = new();

        public List<string> Directors { get; set; } = new();

        public List<string> Producers { get; set; } = new();

        public List<string> Creators { get; set; } = new();

        /// <summary>"Actor (Character)" where the character is known.</summary>
        public List<string> Cast { get; set; } = new();

        /// <summary>Badges on the title page such as "Newly Added".</summary>
        public List<string> Callouts { get; set; } = new();

        public string Url { get; set; } = string.Empty;

        public string ImageUrl { get; set; } = string.Empty;

        /// <summary>Transparent title logo artwork.</summary>
        public string TitleImageUrl { get; set; } = string.Empty;
    }

    public class Movie : TitleDetail
    {
        public int? RuntimeMinutes { get; set; }
    }

    public class TvShow : TitleDetail
    {
        public List<Season> Seasons { get; set; } = new();
    }

    public class Season
    {
        public string SeasonId { get; set; } = string.Empty;

        public string ShowId { get; set; } = string.Empty;

        /// <summary>Order of the season tab on the show page.</summary>
        public int Position { get; set; }

        /// <summary>Parsed from "Season 3"; null when the tab has another name.</summary>
        public int? SeasonNumber { get; set; }

        public string Title { get; set; } = string.Empty;

        /// <summary>Episode count the site shows on the tab ("10 Eps").</summary>
        public int? EpisodeCount { get; set; }

        /// <summary>The tab that was open when the page loaded.</summary>
        public bool IsSelected { get; set; }

        public List<Episode> Episodes { get; set; } = new();

        /// <summary>The database already holds every episode of this season, so it was not reloaded.</summary>
        public bool EpisodesUnchanged { get; set; }

        /// <summary>True when every episode the tab announces was collected.</summary>
        public bool EpisodesComplete => EpisodeCount is int n && Episodes.Count >= n;
    }

    public class Episode
    {
        public string EpisodeId { get; set; } = string.Empty;

        public string ShowId { get; set; } = string.Empty;

        public string SeasonId { get; set; } = string.Empty;

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public DateOnly? AirDate { get; set; }

        public int? RuntimeMinutes { get; set; }

        public string ImageUrl { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;
    }
}
