using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Profile aggregate root — the persistence-side model of the framework store's <c>Profile</c> table,
    /// reachable only through <see cref="IProfileStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Profile is the DEFINITION of one user-profile property (its name, label, validation, display order) —
    /// not a user's VALUE for it. The values live in the Setting cluster under
    /// <c>EntityNames.User</c>. That split is why this cluster is a leaf despite sounding like it belongs to
    /// User: nothing here references a user at all.
    /// </para>
    /// <para>
    /// <b>THE ONE INBOUND REPOSITORY EDGE IN THIS BATCH.</b> <see cref="SiteRepository"/> takes
    /// <see cref="IProfileRepository"/> and seeds eight default profile properties on every site creation. The
    /// edge is INBOUND only and this cluster takes no repository at all, so it cannot close a container cycle
    /// — but it does put this cluster on the site-creation path, which is the installer. Do not
    /// add a repository dependency here without re-running that check; taking
    /// <see cref="ISiteRepository"/> back would deadlock the container.
    /// </para>
    /// <para>
    /// <see cref="SiteId"/> is nullable and the query that matters reads
    /// <c>SiteId == siteId || SiteId == null</c>: a null site means a GLOBAL profile property, visible to
    /// every site. Drop the null arm and the global properties silently vanish from every profile form.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class ProfileAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// The <see cref="Site"/> this property belongs to, by id, or null for a GLOBAL property
        /// visible to every site. The null arm is load-bearing — see the remarks.
        /// </summary>
        public int? SiteId { get; set; }

        /// <summary>The property's name — the key it is stored under in the Setting cluster. NOT the user's name.</summary>
        public string Name { get; set; }

        /// <summary>The label shown on the profile form.</summary>
        public string Title { get; set; }

        /// <summary>Help text for the form. Nullable.</summary>
        public string Description { get; set; }

        /// <summary>The grouping category on the form.</summary>
        public string Category { get; set; }

        /// <summary>The property's position within its category.</summary>
        public int ViewOrder { get; set; }

        /// <summary>The input's maximum length.</summary>
        public int MaxLength { get; set; }

        /// <summary>The value a new user starts with. Nullable.</summary>
        public string DefaultValue { get; set; }

        /// <summary>Whether the form requires a value.</summary>
        public bool IsRequired { get; set; }

        /// <summary>Whether the value is hidden from other users.</summary>
        public bool IsPrivate { get; set; }

        /// <summary>The dropdown's options, when the property has any. Nullable — a later column.</summary>
        public string Options { get; set; }

        /// <summary>An optional validation regex. Nullable — a later column.</summary>
        public string Validation { get; set; }

        /// <summary>
        /// The textarea's row count.
        /// <para>
        /// NON-NULLABLE against a NULLABLE <c>rows</c> column — harvested and reproduced rather than tidied,
        /// exactly as <see cref="SettingAggregate.IsPrivate"/> is. <c>04000601_AddProfileRows</c> added the
        /// column nullable and then backfilled it (<c>UpdateData("Rows", 1)</c>), so no NULL survives for the
        /// non-nullable mapping to choke on. Mapping it <c>int?</c> here would look tidier and would silently
        /// change behaviour: a NULL that today throws on read would start reading as 0.
        /// </para>
        /// </summary>
        public int Rows { get; set; }

        /// <summary>The input's autocomplete hint. Nullable — a later column.</summary>
        public string Autocomplete { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Profile"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int profileId) => Id = profileId;
    }
}
