using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.03.00.01.01")]
    public class ChangeFileNameColumnsSize : MultiDatabaseMigration
    {
        public ChangeFileNameColumnsSize(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var fileEntityBuilder = new FileEntityBuilder(migrationBuilder, ActiveDatabase);
            fileEntityBuilder.AlterStringColumn("Name", 256, false, true, "IX_File:FolderId,Name:true");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var fileEntityBuilder = new FileEntityBuilder(migrationBuilder, ActiveDatabase);
            fileEntityBuilder.AlterStringColumn("Name", 50, false, true, "IX_File:FolderId,Name:true");
        }
    }
}
