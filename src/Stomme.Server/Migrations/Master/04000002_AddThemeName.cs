using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Master
{
    [DbContext(typeof(MasterDBContext))]
    [Migration("Master.04.00.00.02")]
    public class AddThemeName : MultiDatabaseMigration
    {
        public AddThemeName(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var themeEntityBuilder = new ThemeEntityBuilder(migrationBuilder, ActiveDatabase);
            themeEntityBuilder.AddStringColumn("Name", 200, true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // not implemented
        }
    }
}
