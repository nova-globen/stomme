using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.02.00.01")]
    public class AddPageIsClickable : MultiDatabaseMigration
    {
        public AddPageIsClickable(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);

            pageEntityBuilder.AddBooleanColumn("IsClickable", true);
            pageEntityBuilder.UpdateData("IsClickable", true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);

            pageEntityBuilder.DropColumn("IsClickable");
        }
    }
}
