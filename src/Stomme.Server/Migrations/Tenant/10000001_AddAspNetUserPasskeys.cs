using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.10.00.00.01")]
    public class AddAspNetUserPasskeys : MultiDatabaseMigration
    {
        public AddAspNetUserPasskeys(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var aspNetUserPasskeysEntityBuilder = new AspNetUserPasskeysEntityBuilder(migrationBuilder, ActiveDatabase);
            aspNetUserPasskeysEntityBuilder.Create();
            aspNetUserPasskeysEntityBuilder.AddIndex("IX_AspNetUserPasskeys_UserId", "UserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
