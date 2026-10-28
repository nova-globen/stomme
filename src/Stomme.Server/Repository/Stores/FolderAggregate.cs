using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The Folder aggregate root — the persistence-side model of the framework store's <c>Folder</c> table,
    /// reachable only through <see cref="IFolderStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Folder is a site's content directory: a row here plus a real directory on disk, which
    /// <see cref="FolderRepository.GetFolderPathAsync(Folder)"/> resolves from
    /// <see cref="Type"/> + <see cref="SiteId"/> + <see cref="Path"/>. Folders nest through
    /// <see cref="ParentId"/>, and the hierarchy is assembled IN MEMORY by the repository — there is no
    /// recursive query to preserve.
    /// </para>
    /// <para>
    /// <b>IAuditable ONLY — NOT IDeletable — and the EntityBuilder says otherwise.</b> This is the standing
    /// lesson at its sharpest yet. <c>FolderEntityBuilder</c> extends
    /// <c>DeletableAuditableBaseEntityBuilder</c> and calls <c>AddDeletableColumns(table)</c>, so reading it
    /// would tell you Folder is soft-deletable. IT IS NOT. Three migrations take the columns away again:
    /// <c>03020201_RemoveFolderFileDeletableColumns</c> drops all three,
    /// <c>03030201_AddFolderFileIsDeletedColumns</c> restores IsDeleted for non-SQLite so the schemas match, and
    /// <c>10000201_RemoveDeprecatedColumns</c> finally drops it everywhere. VERIFIED against the migrated
    /// PostgreSQL database (stomme-pg-44357): <c>folder</c> has FIFTEEN columns and not one of them is
    /// is_deleted, deleted_by or deleted_on. The contract agrees — <see cref="Folder.IsDeleted"/> is
    /// <c>[NotMapped]</c> + <c>[Obsolete]</c>("Soft delete of folders is not supported").
    /// </para>
    /// <para>
    /// That matters because <see cref="DbContextUtils.SaveChanges"/> decides by RUNTIME TYPE TEST
    /// (<c>item.Entity is IAuditable</c>, <c>item.Entity is IDeletable</c>). Adding IDeletable here would
    /// compile, register and render — and then throw on the first write, because the stamp would target
    /// columns the table does not have. Mutation-checked by <c>FolderAggregateMappingTests</c>.
    /// </para>
    /// <para>
    /// <b>The builder lies about the column LIST too.</b> It creates seven columns and omits
    /// <see cref="Type"/>, <see cref="Capacity"/>, <see cref="ImageSizes"/> and <see cref="CacheControl"/>, all
    /// four of which are real and added by later migrations. It also declares Name and Path as varchar(50)
    /// where the live schema has varchar(256) and varchar(512). The live database is the contract.
    /// </para>
    /// <para>
    /// <b>The UNIQUE index on (site_id, path) is production behaviour, and it does not cross to RavenDB.</b>
    /// <c>ix_folder</c> is what makes <see cref="FolderRepository.GetFolderAsync(int, string)"/> a
    /// single-row lookup rather than a "pick one". Inherited divergence, named where it bites — see
    /// <c>RavenFolderStore</c>.
    /// </para>
    /// <para>
    /// <b>What is NOT here: PermissionList, Level and HasChildren.</b> All three are <c>[NotMapped]</c> view
    /// state on the <see cref="Folder"/> contract that <see cref="FolderRepository"/> populates after this
    /// aggregate has been mapped back — permissions from <see cref="IPermissionRepository"/>, Level and
    /// HasChildren from the in-memory hierarchy walk. They are not persisted and never were.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class FolderAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The <see cref="Site"/> this folder belongs to, by id (D7/D19). NOT NULL, FK-cascaded from Site.</summary>
        public int SiteId { get; set; }

        /// <summary>The parent folder, by id, or null for a site's root. NULLABLE. NOT a navigation — the hierarchy is walked in memory.</summary>
        public int? ParentId { get; set; }

        /// <summary>Private or Public (<c>FolderTypes</c>) — decides which physical root the path resolves under. NULLABLE varchar(50); a later column, absent from the builder.</summary>
        public string Type { get; set; }

        /// <summary>The folder's display name. NOT NULL, varchar(256) — the builder says 50.</summary>
        public string Name { get; set; }

        /// <summary>
        /// The path within the site, e.g. <c>Users/1/</c>. NOT NULL, varchar(512) — the builder says 50. Half
        /// of the UNIQUE index, and the STORED CASE: this is what is displayed and what
        /// <see cref="FolderRepository.GetFolderPathAsync(Folder)"/> resolves to a directory on disk.
        /// </summary>
        /// <remarks>
        /// Setting it maintains <see cref="PathNormalized"/>. That is not a convenience — see the remarks on
        /// <see cref="PathNormalized"/> for why the derivation lives HERE and not in
        /// <see cref="FolderMapper"/>.
        /// </remarks>
        public string Path
        {
            get => _path;
            set
            {
                _path = value;
                PathNormalized = NormalizedText.Normalize(value);
            }
        }

        private string _path;

        /// <summary>
        /// The lowercase lookup key for <see cref="Path"/> (D49) — Identity's <c>NormalizedUserName</c> to
        /// <see cref="Path"/>'s <c>UserName</c>. Column <c>path_normalized</c>, nullable varchar(512), added
        /// and backfilled by <c>10020003_AddFolderFileNormalizedColumns</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It is DERIVED IN THE SETTER of <see cref="Path"/>, and that placement is the decision.</b> The
        /// obvious home was <see cref="FolderMapper"/>, which is where <see cref="FolderRepository"/> maps a
        /// <see cref="Folder"/> in. It is the wrong home: the mapper is not the only way in. The conformance
        /// suite writes aggregates straight through <see cref="IFolderStore"/>, and so may any future caller —
        /// and a write that bypassed the mapper would persist a NULL normalized column, which reads as "this
        /// folder does not exist" on both providers. Silently. Deriving it here makes an unnormalized row
        /// unrepresentable rather than merely discouraged, which is the same standard D49 sets for the read
        /// side (<c>FileRepository</c> normalizes its own arguments so a caller cannot forget).
        /// </para>
        /// <para>
        /// <b>Never map this back onto the <see cref="Folder"/> contract.</b> It is a storage concern, not
        /// wire state — <see cref="FolderMapper"/> deliberately does not round-trip it, exactly as Identity
        /// never shows you a NormalizedUserName. Pinned by
        /// <c>The_mapper_does_not_round_trip_the_normalized_lookup_key</c>.
        /// </para>
        /// <para>
        /// The setter is private so the only way to change it is to change <see cref="Path"/>. Both providers
        /// still populate it when they rehydrate a row — EF Core writes private setters, and RavenDB's
        /// serializer round-trips the public getter — and either way <see cref="Path"/>'s own setter would
        /// re-derive it. That both providers agree is asserted, not assumed:
        /// <c>A_normalized_column_filter_is_identical_on_every_provider</c> runs against a live server of each.
        /// </para>
        /// </remarks>
        public string PathNormalized { get; private set; }

        /// <summary>Sort position. NOT NULL. <c>order</c> is a RESERVED WORD on every relational provider.</summary>
        public int Order { get; set; }

        /// <summary>Image sizes generatable from uploads (e.g. <c>200x200,x200</c>), or <c>*</c> for any. NULLABLE varchar(512); absent from the builder.</summary>
        public string ImageSizes { get; set; }

        /// <summary>Maximum folder capacity in bytes. NULLABLE; absent from the builder.</summary>
        public int Capacity { get; set; }

        /// <summary>Framework dependency — cannot be modified or removed. NOT NULL.</summary>
        public bool IsSystem { get; set; }

        /// <summary>An HTTP Cache-Control directive applied when serving this folder's files. NULLABLE varchar(50); absent from the builder.</summary>
        public string CacheControl { get; set; }

        #region IAuditable

        /// <inheritdoc />
        public string CreatedBy { get; set; }

        /// <inheritdoc />
        public DateTime CreatedOn { get; set; }

        /// <inheritdoc />
        public string ModifiedBy { get; set; }

        /// <inheritdoc />
        public DateTime ModifiedOn { get; set; }

        #endregion

        /// <summary>
        /// Binds the aggregate to an existing row's identity when rehydrating it from a <see cref="Folder"/>
        /// for an update or a delete. The store never calls this for an insert — the database assigns the
        /// identity.
        /// </summary>
        internal void AssignId(int folderId) => Id = folderId;
    }
}
