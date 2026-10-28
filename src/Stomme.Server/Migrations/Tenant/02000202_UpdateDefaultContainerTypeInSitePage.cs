using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.00.02.02")]
    public class UpdateDefaultContainerTypeInSitePage : MultiDatabaseMigration
    {
        public UpdateDefaultContainerTypeInSitePage(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveDatabase.Name == "SqlServer")
            {
                //Update DefaultContainerType In Site
                var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
                siteEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.DefaultTitle, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.Container, Stomme.Client'");
                siteEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.DefaultNoTitle, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.NoTitle, Stomme.Client'");

                //Update DefaultContainerType in Page
                var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);
                pageEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.DefaultTitle, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.Container, Stomme.Client'");
                pageEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.DefaultNoTitle, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.NoTitle, Stomme.Client'");

                //Update ContainerType in PageModule
                var pageModuleEntityBuilder = new PageModuleEntityBuilder(migrationBuilder, ActiveDatabase);
                pageModuleEntityBuilder.UpdateData("ContainerType", "'Stomme.Themes.StommeTheme.DefaultTitle, Stomme.Client'", $"{DelimitName(RewriteName("ContainerType"))} = 'Stomme.Themes.StommeTheme.Container, Stomme.Client'");
                pageModuleEntityBuilder.UpdateData("ContainerType", "'Stomme.Themes.StommeTheme.DefaultNoTitle, Stomme.Client'", $"{DelimitName(RewriteName("ContainerType"))} = 'Stomme.Themes.StommeTheme.NoTitle, Stomme.Client'");
            }

        }
    }
}
