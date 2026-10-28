using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.03.00.01.03")]
    public class AddUrlMappingTable : MultiDatabaseMigration
    {
        public AddUrlMappingTable(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var urlMappingEntityBuilder = new UrlMappingEntityBuilder(migrationBuilder, ActiveDatabase);
            urlMappingEntityBuilder.Create();
            urlMappingEntityBuilder.AddIndex("IX_UrlMapping", new[] { "SiteId", "Url" }, true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var urlMappingEntityBuilder = new UrlMappingEntityBuilder(migrationBuilder, ActiveDatabase);
            urlMappingEntityBuilder.Drop();
        }
    }
}
