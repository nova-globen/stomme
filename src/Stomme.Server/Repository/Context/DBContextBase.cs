using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Stomme.Databases.Interfaces;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Migrations.Framework;
using Stomme.Models;
using Stomme.Shared;

// ReSharper disable BuiltInTypeReferenceStyleForMemberAccess

namespace Stomme.Repository
{
    public class DBContextBase :  DbContext
    {
        private readonly ITenantManager _tenantManager;
        private readonly IHttpContextAccessor _accessor;
        private readonly IConfigurationRoot _config;

        // Null only via the obsolete constructor below, which has no host to ask; the expansion falls back to the
        // process-wide slot there, exactly as it always did (BL-153).
        private readonly IConfigManager _configManager;
        private string _connectionString = "";
        private string _databaseType = "";

        public DBContextBase(IDBContextDependencies DBContextDependencies)
        {
            _tenantManager = DBContextDependencies.TenantManager;
            _accessor = DBContextDependencies.Accessor;
            _config = DBContextDependencies.Config;
            _configManager = DBContextDependencies.ConfigManager;
        }

        public IDatabase ActiveDatabase { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.ReplaceService<IMigrationsAssembly, MultiDatabaseMigrationsAssembly>();

            if (string.IsNullOrEmpty(_connectionString))
            {
                // Sync and reads NO store (D37). OnConfiguring is a sync EF platform virtual Stomme does not
                // own, so it stays sync legitimately ONLY while it touches nothing. GetResolvedTenant reads the
                // alias off SiteState and looks the tenant up in the scope's warmed bindings; it returns null
                // only when there is no alias at all (the installer state handled below), and THROWS rather
                // than guessing when an alias names an unwarmed tenant. See ITenantStoreBindings.
                Tenant tenant = _tenantManager.GetResolvedTenant();
                if (tenant != null)
                {
                    _connectionString = _config.GetConnectionString(tenant.DBConnectionString);
                    if (_connectionString != null)
                    {
                        // THIS host's data directory (BL-153) - see IConfigManager.GetDataDirectory.
                        _connectionString = _connectionString.Replace(
                            $"|{Constants.DataDirectory}|",
                            _configManager?.GetDataDirectory()
                                ?? AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString());
                        _databaseType = tenant.DBType;
                    }
                    else
                    {
                        // tenant connection string does not exist in appsettings.json
                    }
                }
            }

            if (!string.IsNullOrEmpty(_databaseType))
            {
                var type = Type.GetType(_databaseType);
                ActiveDatabase = Activator.CreateInstance(type) as IDatabase;
            }

            // Relational-only EF configuration (D53). ActiveDatabase stays IDatabase because module contexts
            // deriving from this class satisfy IMultiDatabase.ActiveDatabase (the module contract, D55); a
            // non-relational provider is IDatabase but not IRelationalDatabase and skips configuration.
            if (!string.IsNullOrEmpty(_connectionString) && ActiveDatabase is IRelationalDatabase relationalDatabase)
            {
                optionsBuilder.UseStommeDatabase(relationalDatabase, _connectionString);
            }

            base.OnConfiguring(optionsBuilder);
        }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
        }

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

        [Obsolete("This constructor is obsolete. Use DBContextBase(IDBContextDependencies DBContextDependencies) instead.", false)]
        public DBContextBase(ITenantManager tenantManager, IHttpContextAccessor httpContextAccessor)
        {
            _tenantManager = tenantManager;
            _accessor = httpContextAccessor;

            // anti-pattern used to reference config service in base class without causing breaking change
            _config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", false, false)
                .Build();
        }
    }
}
