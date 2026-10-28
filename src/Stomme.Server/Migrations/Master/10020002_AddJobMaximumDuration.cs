using System.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Master
{
    [DbContext(typeof(MasterDBContext))]
    [Migration("Master.10.02.00.02")]
    public class AddJobMaximumDuration : MultiDatabaseMigration
    {
        public AddJobMaximumDuration(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var jobEntityBuilder = new JobEntityBuilder(migrationBuilder, ActiveDatabase);
            jobEntityBuilder.AddIntegerColumn("MaximumDuration", true);
            jobEntityBuilder.UpdateData("MaximumDuration", 30);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
