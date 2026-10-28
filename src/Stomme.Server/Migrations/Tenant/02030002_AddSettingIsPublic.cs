using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.03.00.02")]
    public class AddSettingIsPublic : MultiDatabaseMigration
    {
        public AddSettingIsPublic(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var settingEntityBuilder = new SettingEntityBuilder(migrationBuilder, ActiveDatabase);
            settingEntityBuilder.AddBooleanColumn("IsPublic", true);
            settingEntityBuilder.UpdateData("IsPublic", false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var settingEntityBuilder = new SettingEntityBuilder(migrationBuilder, ActiveDatabase);
            settingEntityBuilder.DropColumn("IsPublic");
        }
    }
}
