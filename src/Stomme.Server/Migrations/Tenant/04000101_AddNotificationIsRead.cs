using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Databases.Interfaces;
using Stomme.Migrations.EntityBuilders;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Migrations.Tenant
{
    [DbContext(typeof(TenantDBContext))]
    [Migration("Tenant.04.00.01.01")]
    public class AddNotificationIsRead : MultiDatabaseMigration
    {

        public AddNotificationIsRead(IDatabase database) : base(database)
        {
        }

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var notificationEntityBuilder = new NotificationEntityBuilder(migrationBuilder, ActiveDatabase);
            notificationEntityBuilder.AddBooleanColumn("IsRead", true);
            notificationEntityBuilder.UpdateData("IsRead", true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var notificationEntityBuilder = new NotificationEntityBuilder(migrationBuilder, ActiveDatabase);
            notificationEntityBuilder.DropColumn("IsRead");
        }

    }


}
