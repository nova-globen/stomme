using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    /// <summary>
    /// Adds the normalized lookup column the UrlMapping cluster needs to AGREE across the persistence seam
    /// (D57): <c>UrlMapping.UrlNormalized</c>, a lowercase copy of <c>Url</c>, and BACKFILLS it for existing
    /// rows. The same shape as <c>10020003_AddFolderFileNormalizedColumns</c> (D49), one cluster on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this column exists — DRIVEN, not inferred.</b> The URL lookup on the render 404 path
    /// (<c>App.razor</c> → <c>UrlMappingRepository.GetUrlMappingAsync</c>) filtered <c>Url</c> raw with
    /// <c>==</c>. That expression means DIFFERENT things on the two providers: a row stored <c>Foo/Bar</c>
    /// looked up as <c>foo/bar</c> MISSES on PostgreSQL (case-SENSITIVE <c>=</c> under the column collation)
    /// and HITS on RavenDB (case-INSENSITIVE <c>==</c>) — observed live on stomme-pg-44357 and a local
    /// RavenDB. D57 removes the disagreement from the DATA rather than the QUERY: store a lowercase copy, then
    /// filter it with plain <c>==</c>, which both providers agree on. The chosen semantics are
    /// case-INSENSITIVE, matching the existing Folder/File precedent (D49).
    /// </para>
    /// <para>
    /// The backfill is the half that matters for upgraders. A fresh install writes every row through
    /// <c>UrlMappingAggregate</c>, whose <c>Url</c> setter derives the normalized value, so it would be correct
    /// with no backfill at all. An UPGRADER would not: its <c>url_mapping</c> rows already carry mixed case,
    /// and an unbackfilled NULL reads as "this URL is unknown" — so the 404 handler would re-capture and
    /// re-increment every already-known URL after upgrading.
    /// </para>
    /// <para>
    /// <b>SQL <c>LOWER()</c></b>, matching what <c>NormalizedText.Normalize</c> (<c>ToLowerInvariant()</c>)
    /// produces on the C# write side, and identical to the Folder/File backfill. All four supported relational
    /// providers have it.
    /// </para>
    /// <para>
    /// NULLABLE, and left nullable — the aggregate makes an unnormalized write unrepresentable, so a NOT NULL
    /// constraint would only ever fire on a row written outside the seam. Consistent with Identity's
    /// <c>NormalizedUserName</c> and with <c>10020003</c>.
    /// </para>
    /// <para>
    /// <b>NO INDEX CHANGE, deliberately.</b> The UNIQUE index <c>ix_url_mapping</c> on (site_id, url) stays
    /// case-SENSITIVE and stays on the stored <c>url</c> column. Moving it onto the normalized twin would
    /// forbid a pair that is legal today (<c>Foo/Bar</c> beside <c>foo/bar</c>) — a silent behaviour change,
    /// and out of scope. Exactly D49's reasoning for <c>ix_folder</c>/<c>ix_file</c>.
    /// </para>
    /// </remarks>
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.02.00.04")]
    public class AddUrlMappingNormalizedColumn : MultiDatabaseMigration
    {
        public AddUrlMappingNormalizedColumn(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 750 mirrors url — the width bounded by the (site_id, url) unique index (MySQL's 3072-byte cap:
            // 750×4+4 = 3004). VERIFIED against the migrated PostgreSQL database (stomme-pg-44357, host
            // 45432): url_mapping.url is varchar(750) NOT NULL, and the table had exactly eight columns before
            // this migration — url_mapping_id, site_id, url, mapped_url, requests, created_on, requested_on,
            // referrer. NOT read off UrlMappingEntityBuilder, which still declares Url as 500.
            var urlMappingEntityBuilder = new UrlMappingEntityBuilder(migrationBuilder, ActiveDatabase);
            urlMappingEntityBuilder.AddStringColumn("UrlNormalized", 750, true);
            urlMappingEntityBuilder.UpdateData("UrlNormalized", Lower("Url"));
        }

        /// <summary>
        /// A <c>LOWER(column)</c> SQL fragment for <paramref name="name"/>, per-provider — identical to
        /// <c>10020003_AddFolderFileNormalizedColumns.Lower</c>. <c>UpdateData</c> passes its value through
        /// <c>IDatabase.RewriteValue</c> (a raw <c>value.ToString()</c> pass-through), so the column reference
        /// is rewritten and delimited HERE.
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
