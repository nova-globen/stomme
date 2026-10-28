using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    public class SearchContentWord
    {
        [Key]
        public int SearchContentWordId { get; set; }

        public int SearchContentId { get; set; }

        public int SearchWordId { get; set; }

        public int Count { get; set; }

        public DateTime CreatedOn { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// The word this row counts. <b>[NotMapped] (D45/D50)</b>: with SearchWordAggregate mapped onto the
        /// `SearchWord` table, a mapped navigation here makes EF rediscover the SearchWord contract THROUGH it
        /// and claim the aggregate's table — the model then fails at startup. View state that
        /// SearchContentRepository.GetSearchContentWordsAsync populates from a second query, and it is
        /// LOAD-BEARING: DatabaseSearchProvider.AnalyzeSearchContentAsync reads SearchWord?.Word to decide
        /// insert-vs-update for every word of every indexed document.
        /// </summary>
        [NotMapped]
        public SearchWord SearchWord { get; set; }

        /// <summary>
        /// The content this row belongs to. <b>[NotMapped] (D45/D50)</b>, for the same reason as
        /// <see cref="SearchWord"/>. Nothing reads it — the harvested search query reached the SearchContent
        /// columns through it, and that query is now ISearchQueryStore's, which joins in the store (EF) or via
        /// a LoadDocument index (RavenDB).
        /// </summary>
        [NotMapped]
        public SearchContent SearchContent { get; set; }
    }
}
