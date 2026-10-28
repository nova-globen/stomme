using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.02.00.02")]
    public class AddLogRemoteIPAddress : MultiDatabaseMigration
    {
        public AddLogRemoteIPAddress(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var logEntityBuilder = new LogEntityBuilder(migrationBuilder, ActiveDatabase);
            logEntityBuilder.AddStringColumn("RemoteIPAddress", 50, true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
