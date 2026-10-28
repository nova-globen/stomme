using System;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Stomme.Models;
using Stomme.Databases.Interfaces;
using Stomme.Extensions;
using Stomme.Migrations.Framework;
using Stomme.Repository.Databases.Interfaces;
using Stomme.Repository.Stores;
using Stomme.Shared;
using System.Threading.Tasks;
using System.Threading;
using Stomme.Infrastructure;

// ReSharper disable BuiltInTypeReferenceStyleForMemberAccess
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable CheckNamespace

namespace Stomme.Repository
{
    public class MasterDBContext : DbContext, IMultiDatabase
    {
        private readonly IHttpContextAccessor _accessor;
        private readonly IConfigManager _config;
        private string _connectionString;
        private string _databaseType;

        public MasterDBContext(DbContextOptions<MasterDBContext> options, IHttpContextAccessor accessor, IConfigManager config) : base(options)
        {
            _accessor = accessor;
            _config = config;
        }

        public IDatabase ActiveDatabase { get; private set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.ReplaceService<IMigrationsAssembly, MultiDatabaseMigrationsAssembly>();

            if(_config != null)
            {
                if (_config.IsInstalled())
                {
                    // THIS host's data directory, not the process-wide slot the last-composed host happened to
                    // write (BL-153, DataDirectoryIsProcessGlobalTests).
                    _connectionString = _config.GetConnectionString(SettingKeys.ConnectionStringKey)
                        .Replace($"|{Constants.DataDirectory}|", _config.GetDataDirectory());
                }

                _databaseType = _config.GetDatabaseType();
            }

            if (!String.IsNullOrEmpty(_databaseType))
            {
                var type = Type.GetType(_databaseType);
                ActiveDatabase = Activator.CreateInstance(type) as IDatabase;
            }

            // Only a relational provider configures an EF provider here (D53). ActiveDatabase stays typed
            // IDatabase because it satisfies IMultiDatabase (the module contract, D55); a RavenDatabase is
            // IDatabase but not IRelationalDatabase, so the pattern-match skips relational configuration.
            if (!string.IsNullOrEmpty(_connectionString) && ActiveDatabase is IRelationalDatabase relationalDatabase)
            {
                optionsBuilder.UseStommeDatabase(relationalDatabase, _connectionString);
            }

            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // The Setting cluster's MASTER half (D23) and the whole master-bookkeeping wave (D52).
            // ConfigureMasterFrameworkStore maps SettingAggregate + JobAggregate + JobLogAggregate + ThemeAggregate
            // + ModuleDefinitionAggregate + TenantAggregate + AliasAggregate — the master-side framework-store
            // aggregates. The other framework-store aggregates are per-tenant tables that the master migration set
            // does not create.
            //
            // ActiveDatabase is set by OnConfiguring, which EF always runs before OnModelCreating. It is null
            // only when the installation has no database configured yet (the first-run installer), and in that
            // state no provider is configured either, so the model is never built. The guard is belt-and-braces
            // for that ordering rather than a live code path.
            if (ActiveDatabase is IRelationalDatabase relationalDatabase)
            {
                builder.ConfigureMasterFrameworkStore(relationalDatabase);
            }
        }

        // NOTE: this context holds ZERO content DbSets — the master-bookkeeping wave (D52) is COMPLETE. There is
        // deliberately no DbSet<Setting>, DbSet<Job>, DbSet<JobLog>, DbSet<Theme>, DbSet<ModuleDefinition>,
        // DbSet<Tenant> or DbSet<Alias>: the master half of the Setting cluster (D23) and the whole master-bookkeeping
        // cluster (D52) are on the seam, mapped above as SettingAggregate/JobAggregate/JobLogAggregate/ThemeAggregate/
        // ModuleDefinitionAggregate/TenantAggregate/AliasAggregate and reached only through their master stores
        // (writes) and IMasterStoreSessionFactory (reads). Deleting DbSet<Tenant> and DbSet<Alias> — the last two — is
        // the compiler's proof the whole cluster left EF's model, the same status as the tenant side's MigrationHistory
        // (D43). The raw install pipeline still writes these tables through the separate InstallationContext, which
        // deliberately keeps its own DbSets for the pre-seam install branch (D53's concern, not this one). See
        // src/Stomme.Server/README.md (persistence seam) and src/Stomme.Persistence.RavenDb/README.md ("THE THIRD WALL").

        public override int SaveChanges()
        {
            DbContextUtils.SaveChanges(this, _accessor);

            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            DbContextUtils.SaveChanges(this, _accessor);

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
