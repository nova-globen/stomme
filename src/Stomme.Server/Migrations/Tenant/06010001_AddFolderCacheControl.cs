using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.06.01.00.01")]
    public class AddFolderCacheControl : MultiDatabaseMigration
    {
        public AddFolderCacheControl(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var folderEntityBuilder = new FolderEntityBuilder(migrationBuilder, ActiveDatabase);
            folderEntityBuilder.AddStringColumn("CacheControl", 50, true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
