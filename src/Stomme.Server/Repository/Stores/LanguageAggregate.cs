using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Language aggregate root — the persistence-side model of the framework store's <c>Language</c>
    /// table, reachable only through <see cref="ILanguageStore"/> (writes) and
    /// <see cref="IQuerySession.Query{T}"/> (reads). See
    /// <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Language is a per-<see cref="Site"/> culture code, at most one of which is the site default. It is a
    /// leaf: nothing navigates into it and it navigates nowhere. Tenant-side only — like Permission (D24) and
    /// unlike Setting (D23), so one store and no <c>IsMaster</c> routing.
    /// </para>
    /// <para>
    /// <b>NOTE WHAT IS ABSENT: <c>Name</c>.</b> The wire contract's <c>Language.Name</c> is <c>[NotMapped]</c>
    /// and there is NO <c>name</c> column — <c>05020401_RemoveLanguageName</c> dropped it for every provider
    /// except SQLite (which could not drop columns then), <c>06000101_AddLanguageName</c> restored it purely so
    /// the two schemas matched, and <c>10000201_RemoveDeprecatedColumns</c> finally dropped it everywhere.
    /// VERIFIED against the migrated PostgreSQL database, whose <c>language</c> table has eight columns and no
    /// <c>name</c>: language_id, site_id, code, is_default and the four audit columns. So <c>Name</c> is not on
    /// this aggregate, and <c>LanguageRepository</c>'s <c>language.Name = ""</c> is a no-op against the database
    /// that only affects the object handed back to the caller. Pinned by
    /// <c>The_aggregate_persists_exactly_the_columns_the_harvested_entity_did</c>.
    /// </para>
    /// <para>
    /// <c>Version</c> (the satellite-assembly version) and <c>AliasName</c> are likewise <c>[NotMapped]</c> view
    /// state with no column, and are absent for the same reason. <c>Language.Version</c> being a
    /// <c>[NotMapped]</c> string is also why this aggregate needs no equivalent of
    /// <see cref="SiteAggregate.SiteVersion"/>: there is no column for <see cref="AggregateRoot{TId}.Version"/>
    /// to collide with.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class LanguageAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>
        /// The <see cref="Site"/> this language belongs to. A reference to another aggregate BY ID (D7/D19).
        /// <para>
        /// NULLABLE on the contract while the <c>site_id</c> column is NOT NULL — harvested and reproduced
        /// rather than tidied. Making it non-nullable here would be a mapping change justified by nothing this
        /// increment measured.
        /// </para>
        /// </summary>
        public int? SiteId { get; set; }

        /// <summary>The culture code ("en-US"), corresponding to <c>Culture.Name</c>.</summary>
        public string Code { get; set; }

        /// <summary>
        /// Whether this is the site's default language. At most one Language per site may have this set, and
        /// NOTHING in the schema enforces it — <see cref="LanguageRepository"/> un-defaults the others by hand
        /// on every add and update. See the remarks there: that un-defaulting is the one piece of this cluster
        /// that depended on EF change tracking.
        /// </summary>
        public bool IsDefault { get; set; }

        public string CreatedBy { get; set; }

        public DateTime CreatedOn { get; set; }

        public string ModifiedBy { get; set; }

        public DateTime ModifiedOn { get; set; }

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Language"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int languageId) => Id = languageId;
    }
}
