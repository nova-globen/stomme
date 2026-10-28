using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.00.02.03")]
    public class AddUrlMappingReferrer : MultiDatabaseMigration
    {
        public AddUrlMappingReferrer(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // the original 10.00.02.03 migration was missing the nullable property specification
            // this would cause it to fail on upgrade so the migration logic was moved to 10.00.03.01
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
