using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.FeatureManagement.Domain;
using Stomme.Infrastructure;
using Stomme.Modules.FeatureManagement.Infrastructure;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.FeatureManagement.Startup
{
    /// <summary>
    /// Wires the feature-flag system into the host (D12). Discovered by the framework's reflection scan the
    /// same way every other module's startup is, so the flag system plugs in by convention rather than by
    /// editing the composition root.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // not implemented
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // not implemented
        }

        public void ConfigureServices(IServiceCollection services)
        {
            // Registered before AddFeatureManagement so the module's anonymous fallback (a TryAdd) stands down.
            services.AddScoped<IFeatureContextProvider, HttpFeatureContextProvider>();

            var configuration = services
                .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?
                .ImplementationInstance as IConfigurationRoot;

            // D73: pick the flag store's provider to MATCH the configured database provider — the module's own
            // SQLite (EF Core) side-car on a relational install, the install's RavenDB document store on a
            // document install, so a Raven site keeps its flags in its document store rather than a stray SQLite
            // file that a document install never provisions. This is a COMPOSITION-time decision (read once here),
            // exactly as D62 selects the OpenIddict store provider: the open-generic IRepository<,> this module
            // owns cannot be swapped per-resolve the way a single closed store interface can (MS.DI has no
            // open-generic factory registration). Consequence — the SAME honest limit D62 carries: the provider
            // must be persisted in config by the time the host boots, which is true for every POST-INSTALL boot
            // (the wizard restarts the host after writing DefaultDBType). Pre-install the seeder defers anyway
            // (the master store is not ready), so no flag is read on the fresh-boot pass that still sees the
            // relational default.
            var databaseType = ResolveConfiguredDatabaseType(configuration);
            if (FrameworkStoreProviderSelection.IsRelationalProvider(databaseType))
            {
                var connectionString = ResolveConnectionString(services, configuration);
                services.AddFeatureManagement(options => options.UseSqlite(connectionString));
                AddBackupParticipants(services);
            }
            else
            {
                // RavenDB: the provider-neutral core, plus the open-generic IRepository<,> pointed at
                // RavenRepository<,>. It resolves the framework store's own lazily-built IDocumentStore (the
                // install's Raven database) and the Raven support services (change-vector tracker, aggregate
                // tracker, id generator, tenant-database resolver) that AddRavenFrameworkStore already TryAdded
                // in the composition root BEFORE this module startup ran — so the flags land in the install's
                // document store, collection-namespaced (FeatureFlags/*, FeatureFlagAudits/*) and tenant-filtered
                // by the TenantId field, exactly the SCOPE the EF model has (one installation-wide store, tenant
                // query filter). This is the re-point D28 forbids the FRAMEWORK store extension from doing (it
                // would clobber the feature flags) but is precisely what the FEATURE module wants on a Raven
                // install: the module owns this open generic, so pointing it at RavenRepository swaps only its
                // own flags, never the framework's closed stores.
                services.AddFeatureManagementCore();
                services.AddScoped(typeof(IRepository<,>), typeof(RavenRepository<,>));

                if (ModuleRepositoryProviderRegistry.ResolvesTo<Stomme.Database.File.FileDatabase>(databaseType))
                {
                    // BL-110 (b), MEASURED: on the File store the branch above composed RavenRepository<,> with no RavenDB
                    // store, so the FIRST flag evaluation threw ("Unable to resolve service for type IDocumentStore").
                    // The File store is the storage-blind module provider (D80), so the two aggregates take the same
                    // path every storage-blind module takes: CLOSED registrations (which win over the open generic
                    // above, D17) through the registry's File provider, with its store root and tracker.
                    services.AddModuleAggregatePersistence<FeatureFlag, Guid>();
                    services.AddModuleAggregatePersistence<FeatureFlagAudit, Guid>();
                }
                else
                {
                    AddBackupParticipants(services);
                }
            }
        }

        // BL-81 (D106 item 3): the flags and their audit in the installation snapshot. The File branch above gets them from
        // AddModuleAggregatePersistence; the EF and RavenDB branches register the same participant explicitly - over the
        // repositories they already bind, with the module's own context for the transaction when there is one.
        private static void AddBackupParticipants(IServiceCollection services)
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant,
                Stomme.Infrastructure.Backup.Participants.ModuleAggregateBackupParticipant<FeatureFlag, Guid, Stomme.FeatureManagement.Infrastructure.FeatureManagementDbContext>>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant,
                Stomme.Infrastructure.Backup.Participants.ModuleAggregateBackupParticipant<FeatureFlagAudit, Guid, Stomme.FeatureManagement.Infrastructure.FeatureManagementDbContext>>());
        }

        /// <summary>
        /// The configured database provider type, read from config the same way
        /// <c>ConfigManager.GetDatabaseType()</c> and D62's OpenIddict branch read it (empty resolves to the
        /// SqlServer default; the pre-6.1.5 <c>, Stomme.Database.*</c> namespace is rewritten to
        /// <c>, Stomme.Server</c>). Read at composition time so the flag store provider matches the database
        /// provider.
        /// </summary>
        private static string ResolveConfiguredDatabaseType(IConfigurationRoot configuration)
        {
            var databaseType = configuration?.GetSection(SettingKeys.DatabaseSection)[SettingKeys.DatabaseTypeKey];
            if (string.IsNullOrEmpty(databaseType))
            {
                return Constants.DefaultDBType;
            }

            if (databaseType.Contains(", Stomme.Database."))
            {
                return databaseType.Substring(0, databaseType.IndexOf(", ", StringComparison.Ordinal)) + ", Stomme.Server";
            }

            return databaseType;
        }

        /// <summary>
        /// The flag store's connection. It is deliberately its own database, not the tenant store: backlog
        /// item 01 has not re-fronted the framework store onto the persistence seam yet, so the module owns
        /// its schema (and creates it) rather than joining a migration pipeline that does not know about it.
        /// Override with <c>ConnectionStrings:FeatureManagement</c>; the default is a SQLite file alongside
        /// the framework's own data.
        /// </summary>
        private static string ResolveConnectionString(IServiceCollection services, IConfigurationRoot configuration)
        {
            var configured = configuration?.GetConnectionString("FeatureManagement");
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = $"Data Source=|{Constants.DataDirectory}|Stomme.Features.db";
            }

            // THIS host's {ContentRoot}/Data. It used to come from the process-wide AppDomain slot, which the
            // last-composed host owned - fine for a deployed app, wrong whenever two hosts coexist (BL-153).
            return HostDataDirectory.Expand(services, configured);
        }
    }
}
