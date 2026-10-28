using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Documentation;
using Stomme.Migrations;
using Stomme.Modules.HtmlText.Migrations.EntityBuilders;
using Stomme.Modules.HtmlText.Repository;

namespace Stomme.Modules.HtmlText.Migrations
{
    [DbContext(typeof(HtmlTextContext))]
    [Migration("HtmlText.01.00.00.00")]
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class InitializeModule : MultiDatabaseMigration
    {
        public InitializeModule(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var entityBuilder = new HtmlTextEntityBuilder(migrationBuilder, ActiveDatabase);
            entityBuilder.Create();
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var entityBuilder = new HtmlTextEntityBuilder(migrationBuilder, ActiveDatabase);
            entityBuilder.Drop();
        }
    }
}
