using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.04.00.00.03")]
    public class AddProfileValidation : MultiDatabaseMigration
    {
        public AddProfileValidation(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var profileEntityBuilder = new ProfileEntityBuilder(migrationBuilder, ActiveDatabase);
            profileEntityBuilder.AddStringColumn("Validation", 200, true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
