using Microsoft.EntityFrameworkCore;
using Stomme.Extensions;
using Stomme.Models;
using IRelationalDatabase = Stomme.Databases.Interfaces.IRelationalDatabase;

// ReSharper disable CheckNamespace
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Stomme.Repository
{

    public class InstallationContext : DbContext
    {
        private readonly string _connectionString;
        private readonly IRelationalDatabase _database;

        public InstallationContext(IRelationalDatabase database, string connectionString)
        {
            _connectionString = connectionString;
            _database = database;
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            // The installer's EF context is relational-only: a non-relational provider yields a null _database
            // (GetInstallationContext casts `as IRelationalDatabase`), which leaves this context unconfigured
            // rather than NRE-ing through UseDatabase. Its callers (IsInstalled/MigrateTenants/MigrateModules)
            // are reached only on the relational install path.
            if (_database != null)
            {
                optionsBuilder.UseStommeDatabase(_database, _connectionString);
            }
        }

        // only contains specific entities which are required for installation in DatabaseManager
        public virtual DbSet<Alias> Alias { get; set; }
        public virtual DbSet<Tenant> Tenant { get; set; }
        public virtual DbSet<ModuleDefinition> ModuleDefinition { get; set; }
    }
}
