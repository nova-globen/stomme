using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.00.01.03")]
    public class UpdatePageAndAddColumnToSite : MultiDatabaseMigration
    {
        public UpdatePageAndAddColumnToSite(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            //Add Column to Site table
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
            siteEntityBuilder.AddStringColumn("AdminContainerType", 200, true);

            //Update new column
            siteEntityBuilder.UpdateData("AdminContainerType", "''");


            //Delete records from Page
            var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);
            pageEntityBuilder.DeleteData($"{ActiveDatabase.RewriteName("Path")} = 'admin/tenants'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            //Drop Column from Site table
            var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
            siteEntityBuilder.DropColumn("AdminContainerType");
        }
    }
}
