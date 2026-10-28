using System.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Master
{
    [DbContext(typeof(MasterDBContext))]
    [Migration("Master.06.01.05.01")]
    public class UpdateTenantDBType : MultiDatabaseMigration
    {
        public UpdateTenantDBType(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // database providers moved to Stomme.Server
            var tenantEntityBuilder = new TenantEntityBuilder(migrationBuilder, ActiveDatabase);
            tenantEntityBuilder.UpdateData("DBType", "'Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Server'", $"{DelimitName(RewriteName("DBType"))} = 'Stomme.Database.SqlServer.SqlServerDatabase, Stomme.Database.SqlServer'");
            tenantEntityBuilder.UpdateData("DBType", "'Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server'", $"{DelimitName(RewriteName("DBType"))} = 'Stomme.Database.Sqlite.SqliteDatabase, Stomme.Database.Sqlite'");
            tenantEntityBuilder.UpdateData("DBType", "'Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server'", $"{DelimitName(RewriteName("DBType"))} = 'Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Database.PostgreSQL'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
