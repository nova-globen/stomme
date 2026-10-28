using System.Collections.Generic;

namespace Stomme.Models
{
    public class SearchResults
    {
        public List<SearchResult> Results { get; set; }

        public int TotalResults { get; set; }
    }
}
