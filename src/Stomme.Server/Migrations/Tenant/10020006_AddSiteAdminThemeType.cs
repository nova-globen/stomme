using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    /// <summary>
    /// Adds the <c>AdminThemeType</c> column to the Site table (D87) — the theme mirror of
    /// <c>AdminContainerType</c> (added by <c>02000103_UpdatePageAndAddColumnToSite</c>). A site renders its
    /// public pages in <c>DefaultThemeType</c> and its admin pages (path "admin"/"admin/...") in
    /// <c>AdminThemeType</c>; empty means "fall back to <c>DefaultThemeType</c>", so an upgraded site with the
    /// column left blank renders exactly as before. Nullable string column, seeded to <c>''</c> for existing
    /// rows — identical shape to the <c>AdminContainerType</c> migration one cluster back. RavenDB is
    /// schemaless and needs no twin migration; the generic <c>RavenFrameworkStore&lt;SiteAggregate&gt;</c>
    /// round-trips the new property for free.
    /// </summary>
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.02.00.06")]
    public class AddSiteAdminThemeType : MultiDatabaseMigration
    {
        public AddSiteAdminThemeType(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
            siteEntityBuilder.AddStringColumn("AdminThemeType", 200, true);
            siteEntityBuilder.UpdateData("AdminThemeType", "''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
            siteEntityBuilder.DropColumn("AdminThemeType");
        }
    }
}
