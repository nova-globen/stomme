using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.00.01.01")]
    public class UpdateIconColumnInPage : MultiDatabaseMigration
    {
        public UpdateIconColumnInPage(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ///Update Icon Field in Page
            var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);
            var updateSql = ActiveDatabase.ConcatenateSql("'oi oi-'", $"{DelimitName(RewriteName("Icon"))}");
            pageEntityBuilder.UpdateData("Icon", updateSql, $"{DelimitName(RewriteName("Icon"))} <> ''" );
        }
    }
}
