using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.03.00.01.04")]
    public class AddSiteVisitorTracking : MultiDatabaseMigration
    {
        public AddSiteVisitorTracking(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);

            siteEntityBuilder.AddBooleanColumn("VisitorTracking", true);
            siteEntityBuilder.UpdateData("VisitorTracking", true);
            siteEntityBuilder.AddBooleanColumn("CaptureBrokenUrls", true);
            siteEntityBuilder.UpdateData("CaptureBrokenUrls", true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);

            siteEntityBuilder.DropColumn("VisitorTracking");
            siteEntityBuilder.DropColumn("CaptureBrokenUrls");
        }
    }
}
