using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.03.00.02.01")]
    public class UpdateSettingIsPublic : MultiDatabaseMigration
    {
        public UpdateSettingIsPublic(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var settingEntityBuilder = new SettingEntityBuilder(migrationBuilder, ActiveDatabase);
            settingEntityBuilder.UpdateData("IsPublic", true, $"{DelimitName(RewriteName("SettingName"))} NOT LIKE 'SMTP%'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var settingEntityBuilder = new SettingEntityBuilder(migrationBuilder, ActiveDatabase);
            settingEntityBuilder.UpdateData("IsPublic", false, $"{DelimitName(RewriteName("SettingName"))} NOT LIKE 'SMTP%'");
        }
    }
}
