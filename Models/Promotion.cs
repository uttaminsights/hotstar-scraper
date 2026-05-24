using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HotstarScraper.Models
{
    public class Promotion
    {
        public string Title { get; set; }

        public string Description { get; set; }

        public string ImageUrl { get; set; }

        public string RedirectUrl { get; set; }

        public DateTime ScrapedAt { get; set; }
    }
}
