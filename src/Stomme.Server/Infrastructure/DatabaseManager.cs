using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stomme.Enums;
using Stomme.Extensions;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ConvertToUsingDeclaration
// ReSharper disable BuiltInTypeReferenceStyleForMemberAccess
// ReSharper disable UseIndexFromEndExpression

namespace Stomme.Infrastructure
{
    public interface IDatabaseManager
    {
        Task<Installation> IsInstalledAsync();
        Task<Installation> InstallAsync();
        Task<Installation> InstallAsync(InstallConfig install);
        Task<Installation> ReconcileConfiguredSitesAsync();
    }

    public class DatabaseManager : IDatabaseManager
    {
        private readonly IConfigManager _config;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ICacheManager _cache;
        private readonly IDistributedLockManager _distributedLockManager;
        private readonly IConfigManager _configManager;
        private readonly ILogger<DatabaseManager> _filelogger;

        public DatabaseManager(IConfigManager config, IServiceScopeFactory serviceScopeFactory, ICacheManager cache, IDistributedLockManager distributedLockManager, IConfigManager configManager, ILogger<DatabaseManager> filelogger)
        {
            _config = config;
            _serviceScopeFactory = serviceScopeFactory;
            _cache = cache;
            _distributedLockManager = distributedLockManager;
            _configManager = configManager;
            _filelogger = filelogger;
        }

        public async Task<Installation> IsInstalledAsync()
        {
            var result = new Installation { Success = false, Message = string.Empty };

            if (!string.IsNullOrEmpty(_config.GetConnectionString(SettingKeys.ConnectionStringKey)))
            {
                if (IsRelationalProvider(_config.GetDatabaseType()))
                {
                    using (var db = GetInstallationContext())
                    {
                        if (await db.Database.CanConnectAsync())
                        {
                            try
                            {
                                // verify master database contains a Tenant table ( ie. validate schema is properly provisioned )
                                var provisioned = await db.Tenant.AnyAsync();
                                result.Success = true;
                            }
                            catch (Exception ex)
                            {
                                result.Message = "Master Database Not Installed Correctly. " + ex.ToString();
                            }
                        }
                        else // cannot connect
                        {
                            try
                            {
                                // get the actual connection error details
                                await db.Database.OpenConnectionAsync();
                            }
                            catch (Exception ex)
                            {
                                result.Message = "Cannot Connect To Master Database. " + ex.ToString();
                            }
                        }
                    }
                }
                else
                {
                    // D54: a non-relational (document-store) install has no EF InstallationContext to probe — its
                    // provider is not IRelationalDatabase, so GetInstallationContext() yields an unconfigured
                    // context that throws on CanConnect. "Installed" is instead determined through the seam: a
                    // master Tenant document exists once CreateTenantAsync has run. A missing database or an
                    // unreachable server is read as "not installed" (Success stays false, with the error
                    // captured), mirroring the relational CanConnect=false arm rather than throwing out of
                    // boot-detect (Program.Main) or the installer's IsInstalled endpoint.
                    try
                    {
                        using (var scope = _serviceScopeFactory.CreateScope())
                        {
                            var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                            result.Success = (await tenants.GetTenantsAsync()).Any();
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Message = "Cannot Connect To Master Database. " + ex.ToString();
                    }
                }
            }
            return result;
        }

        public async Task<Installation> InstallAsync()
        {
            return await InstallAsync(null);
        }

        public async Task<Installation> InstallAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };

            // BL-20: no config validation/rewrite here. Booting must never mutate appsettings.json (D32);
            // the AvailableDatabases catalog (missing section, pre-6.1.5 ", Stomme.Database." entries) is
            // resolved on READ by DatabaseController, the same way GetDatabaseType resolves DefaultDBType.

            // get configuration
            if (install == null)
            {
                // startup or automated installation
                install = new InstallConfig
                {
                    ConnectionString = _config.GetConnectionString(SettingKeys.ConnectionStringKey),
                    TenantName = TenantNames.Master,
                    DatabaseType = _config.GetDatabaseType(),
                    IsNewTenant = false
                };

                var installation = await IsInstalledAsync();
                if (!installation.Success)
                {
                    install.Aliases = GetInstallationConfig(SettingKeys.DefaultAliasKey, string.Empty);
                    install.HostUsername = GetInstallationConfig(SettingKeys.HostUsernameKey, UserNames.Host);
                    // Installation:HostPassword, or the file Installation:HostPasswordFile names (a container secret)
                    var (hostPassword, hostPasswordError) = await UnattendedInstall.ReadHostPasswordAsync(_configManager);
                    install.HostPassword = hostPassword;
                    install.HostEmail = GetInstallationConfig(SettingKeys.HostEmailKey, string.Empty);
                    install.HostName = GetInstallationConfig(SettingKeys.HostNameKey, UserNames.Host);

                    if (hostPasswordError != null)
                    {
                        result.Message = $"Error Installing Master Database For {SettingKeys.ConnectionStringKey}: {hostPasswordError}";
                        install.ConnectionString = "";
                    }
                    else if (!string.IsNullOrEmpty(install.ConnectionString) && !string.IsNullOrEmpty(install.Aliases) && !string.IsNullOrEmpty(install.HostPassword) && !string.IsNullOrEmpty(install.HostEmail))
                    {
                        // automated install
                        install.SiteTemplate = GetInstallationConfig(SettingKeys.SiteTemplateKey, Constants.DefaultSiteTemplate);
                        install.DefaultTheme = GetInstallationConfig(SettingKeys.DefaultThemeKey, Constants.DefaultTheme);
                        install.DefaultContainer = GetInstallationConfig(SettingKeys.DefaultContainerKey, Constants.DefaultContainer);
                        install.SiteName = Constants.DefaultSite;
                        install.IsNewTenant = true;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(install.ConnectionString))
                        {
                            // automated installation is missing required information - named, never the connection string itself,
                            // which carries the database password into the log
                            var missing = new[] { (Name: "DefaultAlias", Value: install.Aliases), (Name: "HostEmail", Value: install.HostEmail), (Name: "HostPassword (or HostPasswordFile)", Value: install.HostPassword) }
                                .Where(setting => string.IsNullOrEmpty(setting.Value)).Select(setting => setting.Name);
                            result.Message = $"Error Installing Master Database For {SettingKeys.ConnectionStringKey}: an automated installation also needs Installation:{string.Join(", Installation:", missing)}.";
                        }
                        install.ConnectionString = "";
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(installation.Message))
                    {
                        // problem with prior installation
                        _filelogger.LogError(Utilities.LogMessage(this, installation.Message));
                        install.ConnectionString = "";
                    }
                }
            }
            else
            {
                // install wizard or add new site
                if (!string.IsNullOrEmpty(install.Aliases))
                {
                    if (string.IsNullOrEmpty(install.SiteTemplate))
                    {
                        install.SiteTemplate = GetInstallationConfig(SettingKeys.SiteTemplateKey, Constants.DefaultSiteTemplate);
                    }
                    if (string.IsNullOrEmpty(install.DefaultTheme))
                    {
                        install.DefaultTheme = GetInstallationConfig(SettingKeys.DefaultThemeKey, Constants.DefaultTheme);
                    }
                    if (string.IsNullOrEmpty(install.DefaultContainer))
                    {
                        install.DefaultContainer = GetInstallationConfig(SettingKeys.DefaultContainerKey, Constants.DefaultContainer);
                    }

                    // add new site
                    if (install.TenantName != TenantNames.Master && install.ConnectionString.Contains("="))
                    {
                        _configManager.AddOrUpdateSetting($"{SettingKeys.ConnectionStringsSection}:{install.TenantName}", install.ConnectionString, true);
                    }
                    if (install.TenantName == TenantNames.Master && !install.ConnectionString.Contains("="))
                    {
                        install.ConnectionString = _config.GetConnectionString(install.ConnectionString);
                    }
                }
                else
                {
                    result.Message = "Invalid Installation Configuration";
                    install.ConnectionString = "";
                }
            }

            // proceed with installation/migration
            if (!string.IsNullOrEmpty(install.ConnectionString))
            {
                var lockKey = "StartUp";

                // Wait for the startup lock. This used to be `while (!TryAcquireLock(...)) { }` - a TIGHT SPIN
                // with an empty body, which on a scale-out install burns a core at 100% for as long as another
                // instance holds the lock (BL-108). It now waits between attempts, and gives up rather than
                // spinning for ever: the lock's own expiry is a minute, so an instance that cannot take it in
                // twice that has lost its holder to a crash, and proceeding is better than never starting.
                // Read IDistributedLockManager's remarks before treating this as exclusion - on a single node it
                // grants unconditionally, and even with scale-out the acquire is not atomic.
                var lockDeadline = DateTime.UtcNow.AddMinutes(2);
                while (!await _distributedLockManager.TryAcquireLockAsync(lockKey, "true", TimeSpan.FromMinutes(1)))
                {
                    if (DateTime.UtcNow >= lockDeadline)
                    {
                        _filelogger.LogWarning(
                            "The startup lock '{LockKey}' was still held after {Minutes} minute(s); proceeding without it. " +
                            "Its holder has most likely crashed - the lock expires on its own after one minute.",
                            lockKey, 2);
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                result = await CreateDatabaseAsync(install);
                if (result.Success)
                {
                    result = await MigrateMasterAsync(install);
                    if (result.Success)
                    {
                        result = await CreateTenantAsync(install);
                        if (result.Success)
                        {
                            result = await MigrateTenantsAsync(install);
                            if (result.Success)
                            {
                                result = await MigrateModulesAsync(install);
                                if (result.Success)
                                {
                                    // D86: persist any module-wide config the installer's onboarding step collected,
                                    // now that MigrateModulesAsync has created the module definitions in the master.
                                    await MigrateModuleSettingsAsync(install);

                                    result = await CreateSiteAsync(install);
                                    if (result.Success)
                                    {
                                        result = await InitializeSitesAsync(install);
                                    }
                                }
                            }
                        }
                    }
                }

                await _distributedLockManager.ReleaseLockAsync(lockKey);
            }

            return result;
        }

        /// <summary>
        /// Creates every <c>Installation:Sites</c> entry that does not exist yet, through the same path the admin's Add Site
        /// takes. Runs on every boot of an installed host, so a site added to the list appears on the next start and a
        /// restart never duplicates one. The whole list is checked first (<see cref="ConfiguredSites.Plan"/>): any problem
        /// refuses the boot by name with nothing created.
        /// </summary>
        public async Task<Installation> ReconcileConfiguredSitesAsync()
        {
            var entries = _configManager.GetSection(ConfiguredSites.Section).Get<List<ConfiguredSite>>() ?? new List<ConfiguredSite>();
            if (entries.Count == 0)
            {
                return new Installation { Success = true, Message = string.Empty };
            }

            var (hostPassword, hostPasswordError) = await UnattendedInstall.ReadHostPasswordAsync(_configManager);
            var state = await ReadConfiguredSitesStateAsync(!string.IsNullOrEmpty(hostPassword) && hostPasswordError == null);
            var (toCreate, problems) = ConfiguredSites.Plan(entries, state);
            if (problems.Count > 0)
            {
                return new Installation { Success = false, Message = $"{ConfiguredSites.Section} is not valid, so no site was created: {string.Join(" ", problems)}" };
            }

            foreach (var (index, entry) in toCreate)
            {
                Tenant existing = null;
                if (!entry.CreatesTenant)
                {
                    using var scope = _serviceScopeFactory.CreateScope();
                    existing = (await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
                        .First(tenant => tenant.Name.Equals(entry.TenantName, StringComparison.OrdinalIgnoreCase));
                }

                var install = new InstallConfig
                {
                    TenantName = existing?.Name ?? entry.TenantName,
                    IsNewTenant = entry.CreatesTenant,
                    DatabaseType = existing?.DBType ?? (string.IsNullOrWhiteSpace(entry.DatabaseType) ? _config.GetDatabaseType() : entry.DatabaseType),
                    ConnectionString = existing?.DBConnectionString ?? entry.ConnectionString,
                    SiteName = entry.Name.Trim(),
                    Aliases = string.Join(",", entry.AliasNames),
                    SiteTemplate = entry.SiteTemplate,
                    DefaultTheme = entry.DefaultTheme,
                    DefaultContainer = entry.DefaultContainer,
                    DefaultAdminContainer = "",
                    RenderMode = entry.RenderMode,
                    Runtime = entry.Runtime,
                    HostEmail = GetInstallationConfig(SettingKeys.HostEmailKey, string.Empty),
                    HostName = GetInstallationConfig(SettingKeys.HostNameKey, UserNames.Host)
                };
                if (entry.CreatesTenant)
                {
                    // A new tenant has an identity store of its own, so the host account is created in it (as Add Site does).
                    install.HostUsername = GetInstallationConfig(SettingKeys.HostUsernameKey, UserNames.Host);
                    install.HostPassword = hostPassword;
                }

                var created = await InstallAsync(install);
                if (!created.Success)
                {
                    return new Installation { Success = false, Message = $"{ConfiguredSites.Section}:{index} ('{install.SiteName}') could not be created: {created.Message}" };
                }
                _filelogger.LogInformation("Created site {Site} from {Entry} ({Aliases})", install.SiteName, $"{ConfiguredSites.Section}:{index}", install.Aliases);
            }

            return new Installation { Success = true, Message = string.Empty };
        }

        private async Task<ConfiguredSitesState> ReadConfiguredSitesStateAsync(bool hostPasswordAvailable)
        {
            List<Alias> aliases;
            List<Tenant> tenants;
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                aliases = (await scope.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync()).ToList();
                tenants = (await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
            }

            // Each tenant's site names, read in a FRESH scope entered through one of its aliases (BL-205: a scope binds its
            // store to the first tenant it serves).
            var siteNames = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var tenant in tenants)
            {
                var alias = aliases.FirstOrDefault(item => item.TenantId == tenant.TenantId && item.SiteId > 0);
                if (alias == null)
                {
                    continue;
                }
                using var scope = _serviceScopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
                siteNames[tenant.Name] = (await scope.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync())
                    .Select(site => site.Name).ToList();
            }

            return new ConfiguredSitesState(
                aliases.Select(alias => (alias.Name, alias.SiteId)).ToList(),
                tenants.Select(tenant => tenant.Name).ToList(),
                siteNames,
                hostPasswordAvailable,
                _config.GetDataDirectory(),
                _config.GetDatabaseType());
        }

        private async Task<Installation> CreateDatabaseAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };

            if (install.IsNewTenant)
            {
                try
                {
                    var databaseType = install.DatabaseType;

                    // get database type
                    var type = Type.GetType(databaseType);

                    if (type != null)
                    {
                        // create database object from type
                        var database = Activator.CreateInstance(type) as Stomme.Databases.Interfaces.IDatabase;

                        // create data directory if does not exist (for LocalDB and SQLite)
                        // THIS host's directory (BL-153) - see IConfigManager.GetDataDirectory.
                        var dataDirectory = _config.GetDataDirectory();
                        if (!Directory.Exists(dataDirectory)) Directory.CreateDirectory(dataDirectory ?? String.Empty);

                        // D53: the create path branches on the provider's storage model. A relational provider
                        // (IRelationalDatabase) uses EF Core's IRelationalDatabaseCreator, which THROWS on any
                        // non-relational provider. A document-store provider (RavenDatabase) has no EF Core
                        // provider and no DbContextOptionsBuilder to hand it — it ensures its database exists
                        // through RavenDatabaseBootstrap instead. The relational branch is byte-for-byte the
                        // harvested behaviour.
                        if (database is Stomme.Databases.Interfaces.IRelationalDatabase relationalDatabase)
                        {
                            var dbOptions = new DbContextOptionsBuilder().UseStommeDatabase(relationalDatabase, NormalizeConnectionString(install.ConnectionString)).Options;
                            using (var dbc = new DbContext(dbOptions))
                            {
                                var databaseCreator = dbc.Database.GetService<IRelationalDatabaseCreator>();
                                if (!await databaseCreator.ExistsAsync())
                                {
                                    // create empty database if it does not exist
                                    await dbc.Database.EnsureCreatedAsync();
                                    result.Success = true;
                                }
                                else
                                {
                                    // assume database is empty and ready for migrations
                                    result.Success = true;
                                }
                            }
                        }
                        else
                        {
                            // D56: unlike the relational branch above — which threads install.ConnectionString
                            // straight into the DbContext and never reads config — a non-relational store is a
                            // LAZY singleton whose ONLY connection-string source is ConnectionStrings:DefaultConnection
                            // (StommeServiceCollectionExtensions.BuildRavenFrameworkDocumentStore). The INTERACTIVE
                            // wizard does not persist that setting until MigrateMasterAsync, which runs AFTER this — so
                            // the store's first build inside EnsureNonRelationalDatabaseAsync would read empty config
                            // and throw (the D54a ordering finding). Persist it now (reload:true is synchronous, so the
                            // store built on the very next line sees it); MigrateMasterAsync's later write is then an
                            // idempotent no-op. The automated path already had DefaultConnection in appsettings, so
                            // this is a no-op there; the relational path never reaches here.
                            if (install.TenantName == TenantNames.Master)
                            {
                                UpdateConnectionString(install.ConnectionString);
                                result.Success = await EnsureNonRelationalDatabaseAsync();
                            }
                            else
                            {
                                // BL-176: a tenant ADDED to a RavenDB installation. Its connection string was saved under its
                                // own name above; its database is decided and created by the tenant loop
                                // (RavenTenantDatabasePlanner). DefaultConnection is NOT rewritten: it names the installation's
                                // own database, where the master rows are - rewriting it with the new tenant's string (as this
                                // branch did before) pointed the next boot at the new tenant's empty database.
                                result.Success = true;
                            }
                        }
                    }
                    else
                    {
                        result.Message = $"The Database Provider {databaseType} Does Not Exist. If This Is A Development Environment Please Ensure You Have Performed A Full Compilation Of All Projects In The Solution Prior To Running The Application.";
                        _filelogger.LogError(Utilities.LogMessage(this, result.Message));
                    }
                }
                catch (Exception ex)
                {
                    result.Message = "An Error Occurred Creating The Database. This Is Usually Related To Your User Not Having Sufficient Rights To Perform This Operation. Please Note That You Can Also Create The Database Manually Prior To Initiating The Install Wizard. " + ex.ToString();
                    _filelogger.LogError(Utilities.LogMessage(this, result.Message));
                }
            }
            else
            {
                result.Success = true;
            }

            return result;
        }

        private async Task<Installation> MigrateMasterAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };

            if (install.TenantName == TenantNames.Master)
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var sql = scope.ServiceProvider.GetRequiredService<ISqlRepository>();

                    var installation = await IsInstalledAsync();
                    try
                    {
                        UpdateInstallation();
                        UpdateConnectionString(install.ConnectionString);
                        UpdateDatabaseType(install.DatabaseType);

                        // D53: EF Core migrations provision the RELATIONAL master schema. A non-relational
                        // provider (RavenDatabase) has no migration set and no MasterDBContext to construct
                        // (OnConfiguring funnels through UseStommeDatabase → UseDatabase, a relational-only
                        // member); its database was already ensured in CreateDatabaseAsync, so there is nothing
                        // to migrate here. The relational branch is unchanged.
                        if (IsRelationalProvider(install.DatabaseType))
                        {
                            using (var masterDbContext = new MasterDBContext(new DbContextOptions<MasterDBContext>(), null, _config))
                            {
                                await AddEFMigrationsHistoryAsync(sql, install.ConnectionString, install.DatabaseType, "", true);
                                // push latest model into database
                                await masterDbContext.Database.MigrateAsync();
                                result.Success = true;
                            }
                        }
                        else
                        {
                            result.Success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Message = "An Error Occurred Provisioning The Master Database. This Is Usually Related To The Master Database Not Being In A Supported State. " + ex.ToString();
                        _filelogger.LogError(Utilities.LogMessage(this, result.Message));
                    }
                }
            }
            else
            {
                result.Success = true;
            }

            return result;
        }

        private async Task<Installation> CreateTenantAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };

            if (!string.IsNullOrEmpty(install.TenantName) && !string.IsNullOrEmpty(install.Aliases))
            {
                // D53: the installer's FIRST writes — the master Tenant and its Aliases — go through the seam's
                // MASTER repositories instead of the raw EF InstallationContext, so a non-relational install can
                // persist them through the same provider-neutral store the resolution READ path already uses
                // (D52). On a relational install this writes IDENTICAL rows: Tenant/Alias are IAuditable, and
                // DbContextUtils stamps CreatedBy/ModifiedBy = "" when there is no authenticated user (install
                // time) — exactly the empty strings the harvested code set by hand — with CreatedOn/ModifiedOn =
                // UtcNow as before. The repositories also invalidate the "Tenants"/"Aliases" caches, which the
                // harvested code did explicitly. (These same repositories are used later in CreateSiteAsync and
                // InitializeSitesAsync, so the seam is known-functional at this install stage.)
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                    var aliases = scope.ServiceProvider.GetRequiredService<IAliasRepository>();

                    Tenant tenant;
                    if (install.IsNewTenant)
                    {
                        tenant = new Tenant
                        {
                            Name = install.TenantName,
                            DBConnectionString = (install.TenantName == TenantNames.Master) ? SettingKeys.ConnectionStringKey : install.TenantName,
                            DBType = install.DatabaseType,
                            CreatedBy = "",
                            CreatedOn = DateTime.UtcNow,
                            ModifiedBy = "",
                            ModifiedOn = DateTime.UtcNow
                        };
                        tenant = await tenants.AddTenantAsync(tenant);
                    }
                    else
                    {
                        tenant = (await tenants.GetTenantsAsync()).FirstOrDefault(item => item.Name == install.TenantName);
                    }

                    var aliasNames = install.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(sValue => sValue.Trim()).ToArray();
                    var firstAlias = aliasNames[0];
                    foreach (var aliasName in aliasNames)
                    {
                        if (tenant != null)
                        {
                            var alias = new Alias
                            {
                                Name = aliasName,
                                TenantId = tenant.TenantId,
                                SiteId = -1,
                                IsDefault = (aliasName == firstAlias),
                                CreatedBy = "",
                                CreatedOn = DateTime.UtcNow,
                                ModifiedBy = "",
                                ModifiedOn = DateTime.UtcNow
                            };
                            // AddAliasAsync lower-cases the stored Name in C# (the routing lookup is
                            // OrdinalIgnoreCase in memory, D52), which the harvested InstallationContext path did
                            // not do at insert — but CreateSiteAsync's UpdateAliasAsync lower-cases it there
                            // anyway, so the final stored casing is unchanged. CreateSiteAsync's alias matching
                            // is OrdinalIgnoreCase to stay robust to that normalization.
                            await aliases.AddAliasAsync(alias);
                        }
                    }
                }
            }

            result.Success = true;

            return result;
        }

        private async Task<Installation> MigrateTenantsAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };

            var versions = Constants.ReleaseVersions.Split(',', StringSplitOptions.RemoveEmptyEntries);

            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var upgrades = scope.ServiceProvider.GetRequiredService<IUpgradeManager>();
                var sql = scope.ServiceProvider.GetRequiredService<ISqlRepository>();
                var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();
                var DBContextDependencies = scope.ServiceProvider.GetRequiredService<IDBContextDependencies>();

                // D54b: enumerate tenants through the seam's Tenant repository (D52) instead of the EF
                // InstallationContext, so a non-relational install reaches this stage instead of throwing on an
                // unconfigured EF context. On a relational install this reads the identical rows the raw
                // db.Tenant enumeration did — the resolution read path already runs through this repository.
                // Materialized so the per-tenant UpdateTenantAsync writes (which invalidate the cache) do not
                // mutate the list mid-iteration.
                var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                foreach (var tenant in (await tenants.GetTenantsAsync()).ToList())
                {
                    tenantManager.SetTenant(tenant);
                    tenant.DBConnectionString = await MigrateConnectionStringAsync(tenants, tenant);
                    try
                    {
                        var connectionString = _configManager.GetSetting($"{SettingKeys.ConnectionStringsSection}:{tenant.DBConnectionString}", "");
                        if (!string.IsNullOrEmpty(connectionString))
                        {
                            // D53: EF Core migrations provision the RELATIONAL tenant schema. A non-relational
                            // tenant (RavenDatabase) has no migration set; its database is ensured through the
                            // Raven bootstrap, not migrated here. The relational branch is unchanged.
                            if (IsRelationalProvider(tenant.DBType))
                            {
                                using (var tenantDbContext = new TenantDBContext(new DbContextOptions<TenantDBContext>(), DBContextDependencies))
                                {
                                    await AddEFMigrationsHistoryAsync(sql, connectionString, tenant.DBType, tenant.Version, false);
                                    // push latest model into database
                                    await tenantDbContext.Database.MigrateAsync();
                                }
                            }
                            else
                            {
                                // BL-176: a RavenDB tenant's rows live in the database its connection string names.
                                await PlanRavenTenantDatabaseAsync(scope.ServiceProvider, tenant, connectionString);
                            }
                        }
                        else
                        {
                            result.Message = "A Connection String Named " + tenant.DBConnectionString + " Does Not Exist For Tenant " + tenant.Name + " In The ConnectionStrings Section Of Appsettings.json";
                            _filelogger.LogError(Utilities.LogMessage(this, result.Message));
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Message = "An Error Occurred Migrating The Database For Tenant " + tenant.Name + ". This Is Usually Related To Database Permissions, Connection String Mappings, Or The Database Not Being In A Supported State. " + ex.ToString();
                        _filelogger.LogError(Utilities.LogMessage(this, result.Message));
                    }

                    if (string.IsNullOrEmpty(result.Message))
                    {
                        // execute any version specific upgrade logic
                        var version = tenant.Version;
                        var index = Array.FindIndex(versions, item => item == version);
                        if (index != (versions.Length - 1))
                        {
                            try
                            {
                                for (var i = (index + 1); i < versions.Length; i++)
                                {
                                    upgrades.Upgrade(tenant, versions[i]);
                                }
                                tenant.Version = versions[versions.Length - 1];
                                // D54b: version write-back through the seam (was db.Entry(tenant).State = Modified).
                                await tenants.UpdateTenantAsync(tenant);
                            }
                            catch (Exception ex)
                            {
                                result.Message = "An Error Occurred Executing Upgrade Logic On Tenant " + tenant.Name + " - " + ex.ToString();
                                _filelogger.LogError(Utilities.LogMessage(this, result.Message));
                            }
                        }
                    }
                }
            }

            result.Success = string.IsNullOrEmpty(result.Message);

            return result;
        }

        private async Task<Installation> MigrateModulesAsync(InstallConfig install)
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var moduleDefinitions = scope.ServiceProvider.GetRequiredService<IModuleDefinitionRepository>();
                var sql = scope.ServiceProvider.GetRequiredService<ISqlRepository>();
                var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();

                // D54b: enumerate tenants and persist module version bumps through the seam (D52) instead of the
                // EF InstallationContext. The raw db.Tenant enumeration here threw HARD on a non-relational
                // install (it is NOT inside the swallow-try/catch below), so it was the concrete stage that
                // broke a Raven install; routing it through ITenantRepository lets the stage complete. On a
                // relational install both read the identical tenant rows.
                var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                var moduleDefinitionStore = scope.ServiceProvider.GetRequiredService<IModuleDefinitionStore>();
                var allTenants = (await tenants.GetTenantsAsync()).ToList();

                foreach (var moduleDefinition in await moduleDefinitions.GetModuleDefinitionsAsync())
                {
                    if (!string.IsNullOrEmpty(moduleDefinition.ReleaseVersions))
                    {
                        var versions = moduleDefinition.ReleaseVersions.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        {
                            var message = "";

                            if (!string.IsNullOrEmpty(moduleDefinition.ServerManagerType))
                            {
                                var moduleType = Type.GetType(moduleDefinition.ServerManagerType);
                                if (moduleType != null)
                                {
                                    foreach (var tenant in allTenants)
                                    {
                                        var index = Array.FindIndex(versions, item => item == moduleDefinition.Version);
                                        if (tenant.Name == install.TenantName && install.TenantName != TenantNames.Master)
                                        {
                                            index = -1;
                                        }
                                        if (index != (versions.Length - 1) && ModuleSupportsDatabase(moduleDefinition.Databases, tenant.DBType))
                                        {
                                            for (var i = (index + 1); i < versions.Length; i++)
                                            {
                                                try
                                                {
                                                    if (moduleType.GetInterface(nameof(IInstallable)) != null)
                                                    {
                                                        tenantManager.SetTenant(tenant);
                                                        var moduleObject = ActivatorUtilities.CreateInstance(scope.ServiceProvider, moduleType) as IInstallable;
                                                        if (moduleObject == null || !await moduleObject.InstallAsync(tenant, versions[i]))
                                                        {
                                                            message = "An Error Occurred Executing IInstallable Interface For " + moduleDefinition.ServerManagerType + " On Tenant " + tenant.Name;
                                                            _filelogger.LogError(Utilities.LogMessage(this, message));
                                                        }
                                                    }
                                                    else
                                                    {
                                                        if (!await sql.ExecuteScriptAsync(tenant, moduleType.Assembly, Utilities.GetTypeName(moduleDefinition.ModuleDefinitionName) + "." + versions[i] + ".sql"))
                                                        {
                                                            message = "An Error Occurred Executing Database Script " + Utilities.GetTypeName(moduleDefinition.ModuleDefinitionName) + "." + versions[i] + ".sql On Tenant " + tenant.Name;
                                                            _filelogger.LogError(Utilities.LogMessage(this, message));
                                                        }
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    message = "An Error Occurred Installing " + moduleDefinition.Name + " Version " + versions[i] + " On Tenant " + tenant.Name + " - " + ex.ToString();
                                                    _filelogger.LogError(Utilities.LogMessage(this, message));
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    message = "An Error Occurred Installing " + moduleDefinition.Name + " - ServerManagerType " + moduleDefinition.ServerManagerType + " Does Not Exist";
                                    _filelogger.LogError(Utilities.LogMessage(this, message));
                                }
                            }

                            // update module if all migrations were successful and version is not current
                            if (string.IsNullOrEmpty(message) && moduleDefinition.Version != versions[versions.Length - 1])
                            {
                                // D54b: version bump written through the seam's ModuleDefinition store (D52)
                                // instead of db.Entry(moduleDefinition).State = Modified on the EF
                                // InstallationContext. The user-customizable Name/Description/Categories already
                                // carry their DB values — GetModuleDefinitionsAsync() (siteId -1) merges them from
                                // the master store — so the harvested EF re-read that "retained" them is redundant
                                // and dropped. This is a plain column update, NOT IModuleDefinitionRepository.
                                // UpdateModuleDefinitionAsync, which dereferences an unresolved alias here (no
                                // alias is set at this install stage) — the D53 finding that deferred this write.
                                moduleDefinition.Version = versions[versions.Length - 1];
                                moduleDefinition.ModifiedOn = DateTime.UtcNow;
                                await moduleDefinitionStore.UpdateAsync(ModuleDefinitionMapper.ToAggregate(moduleDefinition, assignId: true));
                            }
                        }
                    }
                }
            }

            // module migration issues are logged and should not prevent the framework from starting up
            return new Installation { Success = true, Message = string.Empty };
        }

        // D86: write the module-wide configuration the installer's onboarding step collected into the master
        // EntityNames.ModuleDefinition settings channel, keyed by ModuleDefinitionName → ModuleDefinitionId. This
        // runs server-side in the trusted install path (no auth barrier — the wizard never logs in) and AFTER
        // MigrateModulesAsync, so the definitions (and thus their ids) exist. ModuleDefinition settings route to
        // the MASTER database (SettingRepository.IsMaster), which needs no alias context, so this is safe at this
        // pre-CreateSite stage. Empty ModuleSettings (stock/automated install) is a no-op — byte-for-byte unchanged.
        private async Task MigrateModuleSettingsAsync(InstallConfig install)
        {
            if (install.ModuleSettings == null || install.ModuleSettings.Count == 0)
            {
                return;
            }

            try
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var moduleDefinitions = scope.ServiceProvider.GetRequiredService<IModuleDefinitionRepository>();
                    var settings = scope.ServiceProvider.GetRequiredService<ISettingRepository>();

                    var definitions = (await moduleDefinitions.GetModuleDefinitionsAsync()).ToList();
                    foreach (var moduleSetting in install.ModuleSettings)
                    {
                        if (moduleSetting?.Settings == null || string.IsNullOrEmpty(moduleSetting.ModuleDefinitionName))
                        {
                            continue;
                        }

                        var definition = definitions.FirstOrDefault(item => item.ModuleDefinitionName == moduleSetting.ModuleDefinitionName);
                        if (definition == null)
                        {
                            _filelogger.LogError(Utilities.LogMessage(this, $"Module Configuration Skipped - No Module Definition Matches {moduleSetting.ModuleDefinitionName}"));
                            continue;
                        }

                        foreach (var kvp in moduleSetting.Settings)
                        {
                            var existing = await settings.GetSettingAsync(EntityNames.ModuleDefinition, definition.ModuleDefinitionId, kvp.Key);
                            if (existing == null)
                            {
                                await settings.AddSettingAsync(new Setting
                                {
                                    EntityName = EntityNames.ModuleDefinition,
                                    EntityId = definition.ModuleDefinitionId,
                                    SettingName = kvp.Key,
                                    SettingValue = kvp.Value,
                                    IsPrivate = false
                                });
                            }
                            else
                            {
                                existing.SettingValue = kvp.Value;
                                await settings.UpdateSettingAsync(existing);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // module configuration issues are logged and should not prevent the framework from starting up
                _filelogger.LogError(Utilities.LogMessage(this, "An Error Occurred Persisting Module Configuration - " + ex.ToString()));
            }
        }

        private async Task<Installation> CreateSiteAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };

            if (!string.IsNullOrEmpty(install.TenantName) && !string.IsNullOrEmpty(install.Aliases) && !string.IsNullOrEmpty(install.SiteName))
            {
                try
                {
                    using (var scope = _serviceScopeFactory.CreateScope())
                    {
                        // set the alias explicitly so the tenant can be resolved
                        var aliases = scope.ServiceProvider.GetRequiredService<IAliasRepository>();
                        var aliasNames = install.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(sValue => sValue.Trim()).ToArray();
                        var firstAlias = aliasNames[0];
                        var alias = (await aliases.GetAliasesAsync()).FirstOrDefault(item => item.Name.Equals(firstAlias, StringComparison.OrdinalIgnoreCase));
                        var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();
                        await tenantManager.SetAliasAsync(alias);

                        var sites = scope.ServiceProvider.GetRequiredService<ISiteRepository>();
                        var site = (await sites.GetSitesAsync()).FirstOrDefault(item => item.Name == install.SiteName);
                        if (site == null)
                        {
                            var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                            var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
                            var roles = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
                            var userRoles = scope.ServiceProvider.GetRequiredService<IUserRoleRepository>();
                            var folders = scope.ServiceProvider.GetRequiredService<IFolderRepository>();
                            var log = scope.ServiceProvider.GetRequiredService<ILogManager>();
                            var identityUserManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

                            var tenant = (await tenants.GetTenantsAsync()).FirstOrDefault(item => item.Name == install.TenantName);
                            var rendermode = (!string.IsNullOrEmpty(install.RenderMode)) ? install.RenderMode : _configManager.GetSection("RenderMode").Value;
                            var runtime = (!string.IsNullOrEmpty(install.Runtime)) ? install.Runtime : _configManager.GetSection("Runtime").Value;

                            // BL-57: prerender is overridable. Historically it was hardcoded to (rendermode ==
                            // Interactive), which makes an Interactive/WASM site DEAD behind a reverse proxy whose
                            // container cannot resolve its own public host — the SSR prerender pass HTTP-calls the
                            // app's public URL and every page returns an empty 200. An explicit InstallConfig or
                            // appsettings "Prerender" value now wins; absent, the historical default stands.
                            var prerenderSetting = (!string.IsNullOrEmpty(install.Prerender)) ? install.Prerender : _configManager.GetSection("Prerender").Value;
                            var prerender = string.IsNullOrEmpty(prerenderSetting)
                                ? (rendermode == RenderModes.Interactive)
                                : string.Equals(prerenderSetting, "true", StringComparison.OrdinalIgnoreCase);

                            site = new Site
                            {
                                Name = install.SiteName,
                                LogoFileId = null,
                                FaviconFileId = null,
                                PwaIsEnabled = false,
                                PwaAppIconFileId = null,
                                PwaSplashIconFileId = null,
                                AllowRegistration = false,
                                CaptureBrokenUrls = false,
                                VisitorTracking = true,
                                DefaultThemeType = (!string.IsNullOrEmpty(install.DefaultTheme)) ? install.DefaultTheme : Constants.DefaultTheme,
                                DefaultContainerType = (!string.IsNullOrEmpty(install.DefaultContainer)) ? install.DefaultContainer : Constants.DefaultContainer,
                                AdminContainerType = (!string.IsNullOrEmpty(install.DefaultAdminContainer)) ? install.DefaultAdminContainer : Constants.DefaultAdminContainer,
                                SiteTemplateType = install.SiteTemplate,
                                RenderMode = rendermode,
                                Runtime = runtime,
                                Prerender = prerender,
                                Hybrid = false,
                                EnhancedNavigation = true,
                                CultureCode = "en",
                                TenantId = tenant.TenantId // required for site creation
                            };
                            site = await sites.AddSiteAsync(site);

                            if (!string.IsNullOrEmpty(install.HostUsername))
                            {
                                var identityUser = await identityUserManager.FindByNameAsync(install.HostUsername);
                                if (identityUser == null)
                                {
                                    identityUser = new IdentityUser { UserName = install.HostUsername, Email = install.HostEmail, EmailConfirmed = true };
                                    var create = await identityUserManager.CreateAsync(identityUser, install.HostPassword);
                                    if (create.Succeeded)
                                    {
                                        var user = new User
                                        {
                                            SiteId = site.SiteId,
                                            Username = install.HostUsername,
                                            Password = install.HostPassword,
                                            Email = install.HostEmail,
                                            DisplayName = install.HostName,
                                            LastIPAddress = "",
                                            LastLoginOn = null
                                        };
                                        user = await users.AddUserAsync(user);

                                        // add host role
                                        var hostRoleId = (await roles.GetRolesAsync(user.SiteId, true)).FirstOrDefault(item => item.Name == RoleNames.Host)?.RoleId ?? 0;
                                        var userRole = new UserRole { UserId = user.UserId, RoleId = hostRoleId, EffectiveDate = null, ExpiryDate = null, IgnoreSecurityStamp = true };
                                        await userRoles.AddUserRoleAsync(userRole);
                                    }
                                }
                            }

                            foreach (var aliasName in aliasNames)
                            {
                                alias = (await aliases.GetAliasesAsync()).FirstOrDefault(item => item.Name.Equals(aliasName, StringComparison.OrdinalIgnoreCase));
                                if (alias != null)
                                {
                                    alias.SiteId = site.SiteId;
                                    await aliases.UpdateAliasAsync(alias);
                                }
                            }

                            tenant.Version = Constants.Version;
                            await tenants.UpdateTenantAsync(tenant);

                            if (site != null) log.Log(site.SiteId, Shared.LogLevel.Information, this, LogFunction.Create, "Site Created {Site}", site);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Message = "An Error Occurred Creating Site - " + ex.ToString();
                }
            }

            if (string.IsNullOrEmpty(result.Message))
            {
                result.Success = true;
            }
            else
            {
                _filelogger.LogError(Utilities.LogMessage(this, result.Message));
            }

            return result;
        }

        // BL-205: each site is brought up to date in a FRESH DI scope, entered inside its tenant's scope - the shape of
        // SiteContentReconciliationRunner below. This loop used to walk every tenant and site inside ONE DI scope, and a
        // repository resolved there holds a store whose context binds to a tenant's connection on its first use: on an
        // installation with two tenants on two databases, the second tenant's READS went to its own database (a fresh
        // session per read) while its WRITES went through the page store the first tenant's pass had bound - so every page
        // the second tenant lacked was created in the FIRST tenant's database, again on every boot that reconciled pages.
        // Measured on UAT (2026-10-02): every module page three times on the first tenant's site 1. A site's failure is
        // contained to that site and reported, so one tenant whose database is gone no longer stops every site after it.
        private async Task<Installation> InitializeSitesAsync(InstallConfig install)
        {
            var result = new Installation { Success = false, Message = string.Empty };
            var failures = new List<string>();

            try
            {
                List<Tenant> tenants;
                List<Alias> aliases;
                Stomme.Persistence.Abstractions.ITenantScopeFactory tenantScopes;
                using (var probe = _serviceScopeFactory.CreateScope())
                {
                    tenantScopes = probe.ServiceProvider.GetRequiredService<Stomme.Persistence.Abstractions.ITenantScopeFactory>();
                    tenants = (await probe.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
                    aliases = (await probe.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync()).ToList();
                }

                foreach (var tenant in tenants)
                {
                    var tenantId = Tenancy.FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
                    using var tenantScope = tenantId is null ? null : tenantScopes.BeginScope(tenantId.Value);

                    List<Site> sites;
                    try
                    {
                        using var listing = _serviceScopeFactory.CreateScope();
                        listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                        sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync()).ToList();
                    }
                    catch (Exception ex)
                    {
                        failures.Add("An Error Occurred Listing The Sites Of Tenant " + tenant.Name + " - " + ex);
                        continue;
                    }

                    foreach (var site in sites)
                    {
                        var alias = aliases.FirstOrDefault(item => item.TenantId == tenant.TenantId && item.SiteId == site.SiteId && item.IsDefault);
                        if (alias == null)
                        {
                            continue;
                        }

                        try
                        {
                            using var siteScope = _serviceScopeFactory.CreateScope();
                            var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                            tenantManager.SetTenant(tenant);
                            await tenantManager.SetAliasAsync(alias);
                            var siteRepository = siteScope.ServiceProvider.GetRequiredService<ISiteRepository>();

                            // execute site migrations
                            var version = await siteRepository.ProcessSiteMigrationsAsync(alias, site);
                            // process routable modules
                            version = await siteRepository.ProcessPageTemplatesAsync(alias, site, version);
                            // ensure admin pages added by a newer framework release exist on this site —
                            // they are otherwise only ever created at INSTALL, so an upgraded site never
                            // gets them (additive only; an existing admin page is left untouched)
                            await siteRepository.ProcessAdminPageTemplatesAsync(alias, site);
                            if (site.Version != version)
                            {
                                site.Version = version;
                                await siteRepository.UpdateSiteAsync(site);
                            }
                        }
                        catch (Exception ex)
                        {
                            failures.Add("An Error Occurred Initializing Site " + site.Name + " (" + site.SiteId + ") Of Tenant " + tenant.Name + " - " + ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex.ToString());
            }

            if (failures.Count > 0)
            {
                result.Message = "An Error Occurred Initializing Sites - " + string.Join(Environment.NewLine, failures);
            }

            // Module-owned site content (BL-78 notification templates first; D103's shared runner): AFTER the framework
            // steps. It enters each tenant's scope before a fresh DI scope per site and contains every failure per site,
            // so it runs whatever happened to a site above - one broken tenant must not hold back every other's content.
            try
            {
                await new SiteContentReconciliationRunner(_serviceScopeFactory, _filelogger).RunAsync();
            }
            catch (Exception ex)
            {
                _filelogger.LogError(ex, "Site content reconciliation failed; the framework site initialization is unaffected.");
            }

            if (string.IsNullOrEmpty(result.Message))
            {
                result.Success = true;
            }
            else
            {
                _filelogger.LogError(Utilities.LogMessage(this, result.Message));
            }

            return result;
        }

        private string DenormalizeConnectionString(string connectionString)
        {
            var dataDirectory = _config.GetDataDirectory();   // THIS host's directory - BL-153
            connectionString = connectionString.Replace(dataDirectory ?? String.Empty, $"|{Constants.DataDirectory}|");
            return connectionString;
        }

        private InstallationContext GetInstallationContext()
        {
            var connectionString = NormalizeConnectionString(_config.GetConnectionString(SettingKeys.ConnectionStringKey));
            var databaseType = _config.GetDatabaseType();

            // D53: InstallationContext is an EF relational context. Narrow to IRelationalDatabase; a
            // non-relational provider yields null and the context stays unconfigured (it is never the store for
            // a document-store install). Its remaining callers (IsInstalled/MigrateTenants/MigrateModules) are
            // reached only on the relational install path today.
            Stomme.Databases.Interfaces.IRelationalDatabase database = null;
            if (!string.IsNullOrEmpty(databaseType))
            {
                var type = Type.GetType(databaseType);
                database = Activator.CreateInstance(type) as Stomme.Databases.Interfaces.IRelationalDatabase;
            }

            return new InstallationContext(database, connectionString);
        }

        // D53: the storage-model branch predicate. A provider is constructed reflectively (like everywhere else
        // in the install pipeline) and tested for the relational surface. RavenDatabase is IDatabase but not
        // IRelationalDatabase, so it takes the non-relational branch: no IRelationalDatabaseCreator, no
        // MasterDBContext/TenantDBContext MigrateAsync.
        private static bool IsRelationalProvider(string databaseType)
        {
            return CreateDatabaseProvider(databaseType) is Stomme.Databases.Interfaces.IRelationalDatabase;
        }

        private static Stomme.Databases.Interfaces.IDatabase CreateDatabaseProvider(string databaseType)
        {
            if (string.IsNullOrEmpty(databaseType))
            {
                return null;
            }
            var type = Type.GetType(databaseType);
            return type == null ? null : Activator.CreateInstance(type) as Stomme.Databases.Interfaces.IDatabase;
        }

        // D54: a non-relational (document-store) provider ensures its databases exist through the Raven
        // framework-store bootstrap instead of EF Core's IRelationalDatabaseCreator. AddRavenFrameworkStore is
        // now wired into the host (with a LAZY IDocumentStore), so resolving the store here is its first build
        // and the point at which the connection to RavenDB is actually opened. BootstrapRavenFrameworkStoreAsync
        // ensures the framework (and, when configured separately, master) databases exist and deploys the search
        // index — the RavenDB counterpart of the relational path's Database.Migrate(). Idempotent.
        private async Task<bool> EnsureNonRelationalDatabaseAsync()
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                var store = scope.ServiceProvider.GetService<Raven.Client.Documents.IDocumentStore>();
                var options = scope.ServiceProvider.GetService<Stomme.Repository.Stores.RavenFrameworkStoreOptions>();
                if (store != null && options != null)
                {
                    await Stomme.Repository.Stores.RavenFrameworkStoreServiceExtensions.BootstrapRavenFrameworkStoreAsync(store, options);
                    return true;
                }

                _filelogger.LogError(Utilities.LogMessage(this, "A non-relational database provider was selected but no RavenDB document store is registered. The database was not created."));
                return false;
            }
        }

        /// <summary>BL-176: which RavenDB database a tenant's rows live in, decided and made ready - see <see cref="Stomme.Repository.Stores.RavenTenantDatabasePlanner"/>.</summary>
        private async Task PlanRavenTenantDatabaseAsync(IServiceProvider provider, Tenant tenant, string connectionString)
        {
            var planner = new Stomme.Repository.Stores.RavenTenantDatabasePlanner(
                provider.GetRequiredService<Raven.Client.Documents.IDocumentStore>(),
                provider.GetRequiredService<Stomme.Repository.Stores.RavenTenantDatabases>(),
                () => _configManager.GetSetting(Stomme.Repository.Stores.RavenTenantDatabasePlanner.SharedTenantsSetting, string.Empty),
                value => _configManager.AddOrUpdateSetting(Stomme.Repository.Stores.RavenTenantDatabasePlanner.SharedTenantsSetting, value, true),
                _filelogger);
            var aliases = provider.GetRequiredService<IAliasRepository>();
            await planner.PlanAsync(tenant, NormalizeConnectionString(connectionString),
                // A tenant "has sites" when an alias points at a real one: CreateTenantAsync adds a NEW tenant's aliases with SiteId -1
                // before its site exists, which must not read as a tenant from before BL-176.
                async () => (await aliases.GetAliasesAsync()).Any(alias => alias.TenantId == tenant.TenantId && alias.SiteId > 0));
        }

        private string GetInstallationConfig(string key, string defaultValue)
        {
            return _configManager.GetSetting(SettingKeys.InstallationSection, key, defaultValue);
        }

        private string NormalizeConnectionString(string connectionString)
        {
            var dataDirectory = _config.GetDataDirectory();   // THIS host's directory - BL-153
            connectionString = connectionString.Replace($"|{Constants.DataDirectory}|", dataDirectory);
            return connectionString;
        }

        public void UpdateInstallation()
        {
            // BL-20: the deliberate ensure step — mint the InstallationId and stamp version/date here on the
            // install/upgrade path (MigrateMasterAsync), never inside a getter a runtime read can trip.
            _config.EnsureInstallationId();
        }

        public void UpdateConnectionString(string connectionString)
        {
            connectionString = DenormalizeConnectionString(connectionString);
            if (_config.GetConnectionString(SettingKeys.ConnectionStringKey) != connectionString)
            {
                _configManager.AddOrUpdateSetting($"{SettingKeys.ConnectionStringsSection}:{SettingKeys.ConnectionStringKey}", connectionString, true);
            }
        }

        public void UpdateDatabaseType(string databaseType)
        {
            if (_config.GetSetting($"{SettingKeys.DatabaseSection}:{SettingKeys.DatabaseTypeKey}", "") != databaseType)
            {
                _configManager.AddOrUpdateSetting($"{SettingKeys.DatabaseSection}:{SettingKeys.DatabaseTypeKey}", databaseType, true);
            }
        }

        public async Task AddEFMigrationsHistoryAsync(ISqlRepository sql, string connectionString, string databaseType, string version, bool isMaster)
        {
            // in version 2.1.0 the __EFMigrationsHistory tables were introduced and must be added to existing SQL Server installations
            if ((isMaster || (version != null && Version.Parse(version).CompareTo(Version.Parse("2.1.0")) < 0)) && databaseType == Constants.DefaultDBType)
            {
                var script = (isMaster) ? "MigrateMaster.sql" : "MigrateTenant.sql";

                var query = sql.GetScriptFromAssembly(Assembly.GetExecutingAssembly(), script);
                query = query.Replace("{{Version}}", Constants.Version);

                await sql.ExecuteNonQueryAsync(connectionString, databaseType, query);
            }
        }

        public async Task<string> MigrateConnectionStringAsync(ITenantRepository tenants, Tenant tenant)
        {
            // migrate connection strings from the Tenant table to appsettings
            if (tenant.DBConnectionString.Contains("="))
            {
                var defaultConnection = _configManager.GetConnectionString(SettingKeys.ConnectionStringKey);
                if (tenant.DBConnectionString == defaultConnection)
                {
                    tenant.DBConnectionString = SettingKeys.ConnectionStringKey;
                }
                else
                {
                    _configManager.AddOrUpdateSetting($"{SettingKeys.ConnectionStringsSection}:{tenant.Name}", tenant.DBConnectionString, false);
                    tenant.DBConnectionString = tenant.Name;
                }
                // D54b: persist the rewritten reference through the seam's Tenant repository instead of
                // db.Entry(tenant).State = Modified on the EF InstallationContext, so a non-relational install can
                // rewrite it too. Only DBConnectionString changes (never Name), so the master-rename guard in
                // UpdateTenantAsync is inert. On a fresh automated install DBConnectionString is a plain
                // reference ("DefaultConnection"), so this is a no-op — it only rewrites legacy inline strings.
                await tenants.UpdateTenantAsync(tenant);
            }
            return tenant.DBConnectionString;
        }

        // BL-20: ValidateConfiguration is GONE. It rewrote AvailableDatabases at boot (missing section or
        // pre-6.1.5 ", Stomme.Database." entries) from InstallAsync's first statement — before any
        // install-intent check, so serving the wizard mutated appsettings.json. The catalog is now resolved
        // on READ by DatabaseController (defaults + the 6.1.5 namespace migration), the same D32 pattern
        // GetDatabaseType uses for DefaultDBType.

        private bool ModuleSupportsDatabase(string databases, string dbtype)
        {
            // check if module supports tenant database
            if (!string.IsNullOrEmpty(databases))
            {
                foreach (var database in databases.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (dbtype.ToLower().Contains(database.ToLower()))
                    {
                        return true;
                    }
                }
            }
            return string.IsNullOrEmpty(databases);
        }
    }
}
