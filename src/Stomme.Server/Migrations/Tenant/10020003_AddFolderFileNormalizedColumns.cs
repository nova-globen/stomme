using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    /// <summary>
    /// Adds the normalized lookup columns the File cluster needed to cross the persistence seam (D49):
    /// <c>Folder.PathNormalized</c> and <c>File.NameNormalized</c>, each a lowercase copy of the column beside
    /// it, and BACKFILLS them for existing installs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backfill is the half that matters. A fresh install writes every row through
    /// <c>FolderAggregate</c>/<c>FileAggregate</c>, whose setters derive the normalized value, so it would be
    /// correct with no backfill at all. An UPGRADER would not: its <c>folder</c> rows already say
    /// <c>Public/</c> and <c>Users/1/</c>, and an unbackfilled NULL reads as "this folder does not exist" —
    /// every file on the site would 404 after upgrading. Driven against a database with pre-existing
    /// mixed-case rows, not a fresh one; see src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// <para>
    /// <b>SQL <c>LOWER()</c>, matching what the code it replaces already emitted.</b> The harvested reads were
    /// <c>item.Path.ToLower()</c> / <c>item.Name.ToLower()</c>, which EF Core translated to database-side
    /// <c>lower(x)</c> — so backfilling with <c>LOWER()</c> reproduces exactly the values those reads were
    /// already comparing against. All four supported providers have it. <c>NormalizedText.Normalize</c> uses
    /// <c>ToLowerInvariant()</c> on the C# side for the same reason: it must not vary with the host's thread
    /// culture, because the database's <c>LOWER()</c> does not.
    /// </para>
    /// <para>
    /// NULLABLE, and left nullable. There is no NOT NULL constraint to add: the aggregates make an
    /// unnormalized write unrepresentable, so the constraint would only ever fire on a row written by
    /// something outside the seam — which is a bug to find, not a write to reject at 3am. Consistent with
    /// Identity's own <c>NormalizedUserName</c>, which is nullable.
    /// </para>
    /// <para>
    /// <b>NO INDEX CHANGE, deliberately.</b> <c>ix_folder</c> on (site_id, path) and <c>ix_file</c> on
    /// (folder_id, name) stay case-SENSITIVE and stay on the stored columns. Moving either onto its normalized
    /// twin would forbid a pair that is legal today (<c>Docs/</c> beside <c>docs/</c>) — a silent behaviour
    /// change, and out of scope. It is also why the render path's folder resolution returns a LIST.
    /// </para>
    /// </remarks>
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.02.00.03")]
    public class AddFolderFileNormalizedColumns : MultiDatabaseMigration
    {
        public AddFolderFileNormalizedColumns(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Lengths mirror the columns they normalize: folder.path is varchar(512), file.name is
            // varchar(256). VERIFIED against the migrated PostgreSQL database, not read off the EntityBuilders
            // — FolderEntityBuilder still declares both Name and Path as 50, which the live schema has not
            // agreed with since 02010001_ChangeFolderNameAndPathColumnsSize.
            var folderEntityBuilder = new FolderEntityBuilder(migrationBuilder, ActiveDatabase);
            folderEntityBuilder.AddStringColumn("PathNormalized", 512, true);
            folderEntityBuilder.UpdateData("PathNormalized", Lower("Path"));

            var fileEntityBuilder = new FileEntityBuilder(migrationBuilder, ActiveDatabase);
            fileEntityBuilder.AddStringColumn("NameNormalized", 256, true);
            fileEntityBuilder.UpdateData("NameNormalized", Lower("Name"));
        }

        /// <summary>
        /// A <c>LOWER(column)</c> SQL fragment for <paramref name="name"/>, per-provider. <c>UpdateData</c>
        /// passes its value through <c>IDatabase.RewriteValue</c>, which is <c>value.ToString()</c> — a raw
        /// pass-through — so the column reference has to be rewritten and delimited HERE, exactly as
        /// <c>UpdateData</c>'s own <c>condition</c> parameter documents.
        /// </summary>
        private string Lower(string name)
        {
            return $"LOWER({ActiveDatabase.DelimitName(ActiveDatabase.RewriteName(name))})";
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
