using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.03.00.00.01")]
    public class AddSiteRuntime : MultiDatabaseMigration
    {
        public AddSiteRuntime(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
            siteEntityBuilder.AddStringColumn("Runtime", 50, true, true);
            siteEntityBuilder.UpdateData("Runtime", "'Server'");
            siteEntityBuilder.AddStringColumn("RenderMode", 50, true, true);
            siteEntityBuilder.UpdateData("RenderMode", "'ServerPrerendered'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
            siteEntityBuilder.DropColumn("Runtime");
            siteEntityBuilder.DropColumn("RenderMode");
        }
    }
}
