using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    /// <summary>
    /// Describes a Visitor in Stomme.
    /// </summary>
    public class Visitor
    {
        /// <summary>
        /// ID of this Visitor.
        /// </summary>
        public int VisitorId { get; set; }

        /// <summary>
        ///  Reference to a <see cref="Site"/> 
        /// </summary>
        public int SiteId { get; set; }

        /// <summary>
        ///  Reference to a <see cref="User"/> if applicable
        /// </summary>
        public int? UserId { get; set; }

        /// <summary>
        /// Number of times a visitor has visited a site 
        /// </summary>
        public int Visits { get; set; }

        /// <summary>
        /// Last recorded IP Address of visitor
        /// </summary>
        public string IPAddress { get; set; }

        /// <summary>
        /// Last recorded user agent of visitor
        /// </summary>
        public string UserAgent { get; set; }

        /// <summary>
        /// Last recorded language of visitor
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Last recorded Url of visitor
        /// </summary>
        public string Url { get; set; }

        /// <summary>
        /// Last recorded Referrer of visitor
        /// </summary>
        public string Referrer { get; set; }

        /// <summary>
        /// Date the visitor first visited the site
        /// </summary>
        public DateTime CreatedOn { get; set; }

        /// <summary>
        /// Date the visitor last visited the site
        /// </summary>
        public DateTime VisitedOn { get; set; }

        /// <summary>
        /// Direct reference to the <see cref="User"/> object (if applicable). Populated by
        /// <c>VisitorRepository.GetVisitors</c> whenever <see cref="UserId"/> is set; null for anonymous
        /// visitors, exactly as the eager-loaded navigation left it.
        /// </summary>
        /// <remarks>
        /// NOT an EF navigation any more. The User cluster moved onto the persistence seam (D22): the User
        /// table is owned by <c>UserAggregate</c>, and an EF navigation from here would make EF rediscover
        /// <see cref="User"/> as a second entity type on that same table, which breaks the model at startup.
        /// <see cref="UserId"/> is the persisted reference. Visitors are NOT part of the Users/Roles cluster —
        /// <c>VisitorRepository</c> keeps its DbSet, its context and its migrations; only this reference
        /// changed from a navigation to an id. Pinned by <c>VisitorMustNotResurrectUserTests</c>.
        /// </remarks>
        [NotMapped]
        public User User { get; set; }
    }
}
