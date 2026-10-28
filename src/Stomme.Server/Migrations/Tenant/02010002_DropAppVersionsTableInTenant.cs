using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.01.00.02")]
    public class DropAppVersionsTableinTenant : MultiDatabaseMigration
    {
        public DropAppVersionsTableinTenant(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            //Drop AppVersions table
            var appVersionsEntityBuilder = new AppVersionsEntityBuilder(migrationBuilder, ActiveDatabase);
            appVersionsEntityBuilder.Drop();
        }
    }
}
