using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Master
{
    [DbContext(typeof(MasterDBContext))]
    [Migration("Master.03.00.02.01")]
    public class AddAliasIsDefault : MultiDatabaseMigration
    {
        public AddAliasIsDefault(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            //Add Column to Alias table
            var aliasEntityBuilder = new AliasEntityBuilder(migrationBuilder, ActiveDatabase);
            aliasEntityBuilder.AddBooleanColumn("IsDefault", true);
            aliasEntityBuilder.UpdateData("IsDefault", true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var aliasEntityBuilder = new AliasEntityBuilder(migrationBuilder, ActiveDatabase);
            aliasEntityBuilder.DropColumn("IsDefault");
        }
    }
}
