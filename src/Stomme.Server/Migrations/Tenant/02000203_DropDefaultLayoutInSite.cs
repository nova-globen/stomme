using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.02.00.02.03")]
    public class DropDefaultLayoutInSite : MultiDatabaseMigration
    {
        public DropDefaultLayoutInSite(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveDatabase.Name == "SqlServer")
            {
                //Alter Column in Setting table for Sql Server
                var settingEntityBuilder = new SettingEntityBuilder(migrationBuilder, ActiveDatabase);
                settingEntityBuilder.DropIndex("IX_Setting");
                settingEntityBuilder.AlterStringColumn("SettingName", 200);
                settingEntityBuilder.AddIndex("IX_Setting", new [] {"EntityName", "EntityId", "SettingName"}, true);

                //Drop Column from Site Table
                var siteEntityBuilder = new SiteEntityBuilder(migrationBuilder, ActiveDatabase);
                siteEntityBuilder.DropColumn("DefaultLayoutType");

                //Update DefaultContainerType In Site
                siteEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.Container, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.DefaultTitle, Stomme.Client'");
                siteEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.Container, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.DefaultNoTitle, Stomme.Client'");

                //Drop Column from Page Table
                var pageEntityBuilder = new PageEntityBuilder(migrationBuilder, ActiveDatabase);
                pageEntityBuilder.DropColumn("LayoutType");

                //Update DefaultContainerType in Page
                pageEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.Container, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.DefaultTitle, Stomme.Client'");
                pageEntityBuilder.UpdateData("DefaultContainerType", "'Stomme.Themes.StommeTheme.Container, Stomme.Client'", $"{DelimitName(RewriteName("DefaultContainerType"))} = 'Stomme.Themes.StommeTheme.DefaultNoTitle, Stomme.Client'");


                //Update ContainerType in PageModule
                var pageModuleEntityBuilder = new PageModuleEntityBuilder(migrationBuilder, ActiveDatabase);
                pageModuleEntityBuilder.UpdateData("ContainerType", "'Stomme.Themes.StommeTheme.Container, Stomme.Client'", $"{DelimitName(RewriteName("ContainerType"))} = 'Stomme.Themes.StommeTheme.DefaultTitle, Stomme.Client'");
                pageModuleEntityBuilder.UpdateData("ContainerType", "'Stomme.Themes.StommeTheme.Container, Stomme.Client'", $"{DelimitName(RewriteName("ContainerType"))} = 'Stomme.Themes.StommeTheme.DefaultNoTitle, Stomme.Client'");

            }
        }
    }
}
