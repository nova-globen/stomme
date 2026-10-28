using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Repository;

namespace Stomme.Modules
{
    public class MigratableModuleBase
    {
        public async Task<bool> MigrateAsync(DBContextBase dbContext, Tenant tenant, MigrationType migrationType)
        {
            var result = true;

            using (dbContext)
            {
                var migrator = dbContext.GetService<IMigrator>();
                if (migrationType == MigrationType.Down)
                {
                    await migrator.MigrateAsync(Migration.InitialDatabase);
                }
                else
                {
                    await migrator.MigrateAsync();
                }
            }

            return result;
        }
    }
}
