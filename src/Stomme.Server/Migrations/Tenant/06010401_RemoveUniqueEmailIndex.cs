using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.06.01.04.01")]
    public class RemoveUniqueEmailIndex : MultiDatabaseMigration
    {
        public RemoveUniqueEmailIndex(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // framework uses RequireUniqueEmail = False in .NET Identity configuration
            var aspNetUsersEntityBuilder = new AspNetUsersEntityBuilder(migrationBuilder, ActiveDatabase);
            aspNetUsersEntityBuilder.DropIndex("EmailIndex");
            aspNetUsersEntityBuilder.AddIndex("EmailIndex", "NormalizedEmail", false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
