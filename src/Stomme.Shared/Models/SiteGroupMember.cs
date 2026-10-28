using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Stomme.Models
{
    public class SiteGroupMember : ModelBase
    {
        /// <summary>
        /// ID to identify the site group
        /// </summary>
        public int SiteGroupMemberId { get; set; }

        /// <summary>
        /// Reference to the <see cref="SiteGroup"/>.
        /// </summary>
        public int SiteGroupId { get; set; }

        /// <summary>
        /// Reference to the <see cref="Site"/>.
        /// </summary>
        public int SiteId { get; set; }

        /// <summary>
        /// The last date/time the site was synchronized
        /// </summary>
        public DateTime? SynchronizedOn { get; set; }

        /// <summary>
        /// The <see cref="SiteGroup"/> itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>VIEW STATE, NOT A NAVIGATION (D45).</b> This was a mapped EF navigation that
        /// <c>SiteGroupMemberRepository</c> eager-loaded with <c>.Include</c>. Both SiteGroup and
        /// SiteGroupMember are now on the persistence seam, and while this member stayed mapped EF rediscovered
        /// the <see cref="Models.SiteGroup"/> CLR type THROUGH it and claimed the table
        /// <c>SiteGroupAggregate</c> owns — the model then failed at startup with "The table 'site_group'
        /// cannot be used for entity type 'SiteGroupAggregate' since it is being used for entity type
        /// 'SiteGroup'". That was observed, not assumed.
        /// </para>
        /// <para>
        /// It is still POPULATED — by <c>SiteGroupMemberRepository</c>, from a second query, exactly where the
        /// <c>.Include</c> used to run. Twenty-two production reads depend on it
        /// (<c>SynchronizationJob</c>, <c>ServerSiteService</c>, Admin <c>Site/Index.razor</c>), so do not
        /// remove it. What changed is who fills it in, not whether it is there.
        /// </para>
        /// </remarks>
        [NotMapped]
        public SiteGroup SiteGroup { get; set; }

        /// <summary>
        /// The primary alias for the site 
        /// </summary>
        [NotMapped]
        public string AliasName { get; set; }
    }
}
