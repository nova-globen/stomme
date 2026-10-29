using System;
using Stomme.Models;
using Stomme.SharedKernel.Domain;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// The File aggregate root — the persistence-side model of the framework store's <c>File</c> table,
    /// reachable only through <see cref="IFileStore"/> (writes) and <see cref="IQuerySession.Query{T}"/>
    /// (reads). See <c>src/Stomme.Server/README.md (persistence seam)</c> for the cluster boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A File is a row here plus a real file on disk, which
    /// <see cref="FileRepository.GetFilePathAsync(Models.File)"/> resolves from its folder's path + this
    /// row's <see cref="Name"/>. It is the LAST framework cluster to cross the seam, and this is why it was
    /// late: its two name lookups folded case on the column, which no portable expression could say. The fix
    /// removed the disagreement from the data instead — see <see cref="NameNormalized"/>.
    /// </para>
    /// <para>
    /// <b>IAuditable ONLY — NOT IDeletable — and <c>FileEntityBuilder</c> says otherwise.</b> The builder
    /// extends <c>DeletableAuditableBaseEntityBuilder</c> and calls <c>AddDeletableColumns(table)</c>, so
    /// reading it would tell you File is soft-deletable. IT IS NOT. Three migrations take the columns away
    /// again — <c>03020201_RemoveFolderFileDeletableColumns</c> drops all three,
    /// <c>03030201_AddFolderFileIsDeletedColumns</c> restores IsDeleted for non-SQLite so the schemas match,
    /// and <c>10000201_RemoveDeprecatedColumns</c> finally drops it everywhere. VERIFIED against the migrated
    /// PostgreSQL database (stomme-pg-44357): <c>file</c> has TWELVE columns and not one of them is
    /// is_deleted, deleted_by or deleted_on — the three attnum slots they occupied are dropped stubs. The
    /// contract agrees: <see cref="Models.File.IsDeleted"/> is <c>[NotMapped]</c> + <c>[Obsolete]</c>("Soft
    /// delete of files is not supported"). Exactly the Folder finding, re-measured rather than
    /// inherited.
    /// </para>
    /// <para>
    /// That matters because <see cref="DbContextUtils.SaveChanges"/> decides by RUNTIME TYPE TEST
    /// (<c>item.Entity is IAuditable</c>, <c>item.Entity is IDeletable</c>). Adding IDeletable here would
    /// compile, register and render — and then throw on the first write, because the stamp would target
    /// columns the table does not have. Mutation-checked by <c>FileAggregateMappingTests</c>.
    /// </para>
    /// <para>
    /// <b>The builder omits <see cref="Description"/></b>, which is real (varchar(512), nullable, added by a
    /// later migration). The live database is the contract.
    /// </para>
    /// <para>
    /// <b>What is NOT here: Folder and Url.</b> Both are <c>[NotMapped]</c> view state on the
    /// <see cref="Models.File"/> contract that <see cref="FileRepository"/> populates after this aggregate has
    /// been mapped back — the folder from <see cref="IFolderRepository"/> (the navigation is severed), the
    /// url computed from it. They are not persisted and never were.
    /// </para>
    /// <para>
    /// <b>The UNIQUE index on (folder_id, name) is production behaviour and does not cross to RavenDB.</b>
    /// <c>ix_file</c> is case-SENSITIVE, exactly as Folder's <c>ix_folder</c> on (site_id, path) is — so
    /// <c>Logo.png</c> and <c>logo.png</c> may legitimately coexist in one folder, and the normalized column does NOT change
    /// that. It is why the render path's folder resolution returns a LIST. See <c>RavenFileStore</c>.
    /// </para>
    /// <para>
    /// The model is anemic and raises no domain events — a faithful port, not a re-modelling.
    /// </para>
    /// </remarks>
    public class FileAggregate : AggregateRoot<int>, IAuditable
    {
        /// <summary>The <see cref="FolderAggregate"/> this file belongs to, by id. NOT NULL, FK-cascaded from Folder.</summary>
        public int FolderId { get; set; }

        /// <summary>
        /// The file's name, e.g. <c>Test-Image.PNG</c>. NOT NULL, varchar(256). The STORED CASE — this is what
        /// is displayed and what names the file on disk.
        /// </summary>
        /// <remarks>
        /// Setting it maintains <see cref="NameNormalized"/>. See the remarks there for why the derivation
        /// lives HERE and not in <see cref="FileMapper"/>.
        /// </remarks>
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                NameNormalized = NormalizedText.Normalize(value);
            }
        }

        private string _name;

        /// <summary>
        /// The lowercase lookup key for <see cref="Name"/> — Identity's <c>NormalizedUserName</c> to
        /// <see cref="Name"/>'s <c>UserName</c>. Column <c>name_normalized</c>, nullable varchar(256), added
        /// and backfilled by <c>10020003_AddFolderFileNormalizedColumns</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This column is why the File cluster could move at all.</b> The render path
        /// (<c>Pages/Files.cshtml.cs</c>) lowercases a requested URL and looks the file up by it, while the
        /// database stores mixed case. The harvested read said <c>item.Name.ToLower() == fileName</c>, which
        /// RavenDB cannot translate (measured). Filtering this column with plain <c>==</c> says the same
        /// thing on both providers — see <see cref="NormalizedText"/> for the equivalence and the test that
        /// asserts it live on each.
        /// </para>
        /// <para>
        /// <b>Derived in <see cref="Name"/>'s setter, not in the mapper, for the reason
        /// <see cref="FolderAggregate.PathNormalized"/> spells out:</b> the mapper is not the only way in, and
        /// a write that bypassed it would persist NULL here, which reads as "this file does not exist" on both
        /// providers, silently. Unrepresentable beats discouraged.
        /// </para>
        /// <para>
        /// Never mapped back onto the <see cref="Models.File"/> contract — storage concern, not wire state.
        /// </para>
        /// </remarks>
        public string NameNormalized { get; private set; }

        /// <summary>File name extension without the dot, e.g. <c>png</c>. NOT NULL, varchar(50). Already lowercase by the contract's own documentation, so it needs no normalized twin — nothing filters on it.</summary>
        public string Extension { get; set; }

        /// <summary>File size in bytes. NOT NULL.</summary>
        public int Size { get; set; }

        /// <summary>The height of an image in pixels, calculated at upload time. NOT NULL.</summary>
        public int ImageHeight { get; set; }

        /// <summary>The width of an image in pixels, calculated at upload time. NOT NULL.</summary>
        public int ImageWidth { get; set; }

        /// <summary>A description of the file. NULLABLE varchar(512); a later column, absent from the builder.</summary>
        public string Description { get; set; }

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
        /// Binds the aggregate to an existing row's identity when rehydrating it from a
        /// <see cref="Models.File"/> for an update or a delete. The store never calls this for an insert — the
        /// database assigns the identity.
        /// </summary>
        internal void AssignId(int fileId) => Id = fileId;
    }
}
