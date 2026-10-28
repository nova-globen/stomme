using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.05.01.00.04")]
    public class AddSitePrerender : MultiDatabaseMigration
    {
        public AddSitePrerender(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);

            siteEntityBuilder.UpdateData("RenderMode", $"'{RenderModes.Interactive}'");

            siteEntityBuilder.AddBooleanColumn("Prerender", true);
            siteEntityBuilder.UpdateData("Prerender", true);

            siteEntityBuilder.AddBooleanColumn("Hybrid", true);
            siteEntityBuilder.UpdateData("Hybrid", false);
            siteEntityBuilder.DropColumn("HybridEnabled");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
