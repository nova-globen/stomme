using System.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Master
{
    [DbContext(typeof(MasterDBContext))]
    [Migration("Master.10.02.00.01")]
    public class AddJobLogServer : MultiDatabaseMigration
    {
        public AddJobLogServer(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var jobLogEntityBuilder = new JobLogEntityBuilder(migrationBuilder, ActiveDatabase);
            jobLogEntityBuilder.AddStringColumn("Server", 200, true);
            jobLogEntityBuilder.AddStringColumn("Instance", 200, true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
