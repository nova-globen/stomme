using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    /// <summary>
    /// Adds the normalized lookup column the Page cluster needs to AGREE across the persistence seam:
    /// <c>Page.PathNormalized</c>, a lowercase copy of <c>Path</c>, and BACKFILLS it for existing rows. The
    /// same shape as <c>10020004_AddUrlMappingNormalizedColumn</c> and <c>10020003</c>, one
    /// cluster on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this column exists — DRIVEN, not inferred.</b> The page-path fallback lookup on the render
    /// RESOLVE path (<c>SiteRouter.razor:241</c>, reached when the in-memory <c>OrdinalIgnoreCase</c> match at
    /// <c>:227</c> misses) and the admin page lookup (<c>PageController.Get</c>) both funnel through
    /// <c>PageRepository.GetPageAsync(path, siteId)</c>, which filtered <c>Path</c> raw with <c>==</c>. That
    /// expression means DIFFERENT things behind the seam: a row stored <c>Foo/Bar</c> looked up as
    /// <c>foo/bar</c> MISSES on PostgreSQL (case-SENSITIVE <c>=</c> under the column collation) and HITS on
    /// RavenDB (case-INSENSITIVE <c>==</c>) — observed live on a PostgreSQL install and a local RavenDB. Storing a
    /// normalized copy removes the disagreement from the DATA rather than the QUERY: store a lowercase copy, then filter it
    /// with plain <c>==</c>, which both providers agree on. The chosen semantics are case-INSENSITIVE, which
    /// also MATCHES <c>SiteRouter</c>'s own primary in-memory match
    /// (<c>StringComparison.OrdinalIgnoreCase</c>) and the Folder/File/UrlMapping precedent.
    /// </para>
    /// <para>
    /// The backfill is the half that matters for upgraders. A fresh install writes every row through
    /// <c>PageAggregate</c>, whose <c>Path</c> setter derives the normalized value, so it would be correct
    /// with no backfill at all. An UPGRADER would not: its <c>page</c> rows already carry their stored case,
    /// and an unbackfilled NULL reads as "this page is unknown" — so the fallback lookup would silently miss
    /// after upgrading.
    /// </para>
    /// <para>
    /// <b>SQL <c>LOWER()</c></b>, matching what <c>NormalizedText.Normalize</c> (<c>ToLowerInvariant()</c>)
    /// produces on the C# write side, and identical to the Folder/File/UrlMapping backfill. All four supported
    /// relational providers have it.
    /// </para>
    /// <para>
    /// varchar(256) mirrors <c>page.path</c> on the non-SQL-Server providers (verified against a migrated
    /// PostgreSQL database: <c>page.path</c> is varchar(256)); on SQL Server
    /// <c>page.path</c> is varchar(50), so 256 is comfortably wider than any value <c>LOWER(path)</c> can
    /// yield there. NULLABLE, and left nullable — the aggregate makes an unnormalized write unrepresentable,
    /// consistent with Identity's <c>NormalizedUserName</c> and with <c>10020003</c>/<c>10020004</c>.
    /// </para>
    /// <para>
    /// <b>NO INDEX CHANGE, deliberately.</b> The <c>(site_id, path)</c> uniqueness stays case-SENSITIVE on the
    /// stored <c>path</c> column; the normalized column is a LOOKUP key only, mirroring the Folder/File/UrlMapping columns.
    /// </para>
    /// </remarks>
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.02.00.05")]
    public class AddPageNormalizedColumn : MultiDatabaseMigration
    {
        public AddPageNormalizedColumn(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);
            pageEntityBuilder.AddStringColumn("PathNormalized", 256, true);
            pageEntityBuilder.UpdateData("PathNormalized", Lower("Path"));
        }

        /// <summary>
        /// A <c>LOWER(column)</c> SQL fragment for <paramref name="name"/>, per-provider — identical to
        /// <c>10020004_AddUrlMappingNormalizedColumn.Lower</c>. <c>UpdateData</c> passes its value through
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
