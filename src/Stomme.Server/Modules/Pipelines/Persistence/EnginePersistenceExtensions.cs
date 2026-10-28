#nullable enable
using System;
using Stomme.Database.RavenDb;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Features;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Features;
using Stomme.Modules.Pipelines.Persistence.EfCore;
using Stomme.Modules.Pipelines.Persistence.Raven;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using EngineModule = Stomme.Engine.Features.Services.IModule;

namespace Stomme.Modules.Pipelines.Persistence
{
    /// <summary>Which of the two store families the install's provider selected.</summary>
    public enum EngineStoreKind
    {
        /// <summary>SQLite, PostgreSQL or SQL Server - the EF Core stores over <see cref="EngineDbContext"/>.</summary>
        Relational,

        /// <summary>RavenDB - the document stores over the install's document store.</summary>
        RavenDb,
    }

    /// <summary>
    /// The engine's nine store contracts on Stomme persistence (BL-95 increment (a), D111). The provider is read
    /// from <c>Database:DefaultDBType</c> at composition time exactly as the storage-blind modules read it: the three
    /// relational providers get the EF Core stores in the module store's database, RavenDB gets the document
    /// stores, and any other provider fails LOUD here rather than leaving the engine on its in-memory stores - a
    /// pipeline persisted to memory is lost on the next restart, which is not a state a product can ship in.
    /// </summary>
    public static class EnginePersistenceExtensions
    {
        public static EngineStoreKind AddEnginePersistence(this IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            var databaseType = ModuleAggregatePersistenceExtensions.ResolveConfiguredDatabaseType(services);
            services.TryAddSingleton<EngineEntitySerializer>();

            if (FrameworkStoreProviderSelection.IsRelationalProvider(databaseType))
            {
                AddRelationalStores(services, databaseType);
                AddBackup(services);
                return EngineStoreKind.Relational;
            }

            if (ModuleRepositoryProviderRegistry.ResolvesTo<RavenDatabase>(databaseType))
            {
                AddRavenStores(services);
                AddBackup(services);
                return EngineStoreKind.RavenDb;
            }

            throw new NotSupportedException(
                $"The pipeline engine's stores have no implementation for Database:DefaultDBType '{databaseType}'. " +
                "Supported: SQLite, PostgreSQL and SQL Server (the relational stores) and RavenDB (the document stores). " +
                "The File store is not among them (D111); disable Pipelines:Enabled on such an install.");
        }

        /// <summary>Points every engine store factory at the Stomme-backed implementation of the chosen kind.</summary>
        public static EngineModule UseStommeStores(this EngineModule module, EngineStoreKind kind)
        {
            ArgumentNullException.ThrowIfNull(module);
            module.Configure<PipelineDefinitionsFeature>(feature =>
                feature.PipelineDefinitionStore = Pick<IPipelineDefinitionStore, EfPipelineDefinitionStore, RavenPipelineDefinitionStore>(kind));
            module.Configure<PipelineInstancesFeature>(feature =>
                feature.PipelineInstanceStore = Pick<IPipelineInstanceStore, EfPipelineInstanceStore, RavenPipelineInstanceStore>(kind));
            module.Configure<PipelineRuntimeFeature>(feature =>
            {
                feature.BookmarkStore = Pick<IBookmarkStore, EfBookmarkStore, RavenBookmarkStore>(kind);
                feature.BookmarkQueueStore = Pick<IBookmarkQueueStore, EfBookmarkQueueStore, RavenBookmarkQueueStore>(kind);
                feature.TriggerStore = Pick<ITriggerStore, EfTriggerStore, RavenTriggerStore>(kind);
                feature.StepExecutionLogStore = Pick<IStepExecutionStore, EfStepExecutionStore, RavenStepExecutionStore>(kind);
                feature.PipelineExecutionLogStore = Pick<IPipelineExecutionLogStore, EfPipelineExecutionLogStore, RavenPipelineExecutionLogStore>(kind);
            });
            module.Configure<KeyValueFeature>(feature =>
                feature.KeyValueStore = Pick<IKeyValueStore, EfKeyValueStore, RavenKeyValueStore>(kind));
            return module;
        }

        private static Func<IServiceProvider, TContract> Pick<TContract, TRelational, TRaven>(EngineStoreKind kind)
            where TRelational : class, TContract
            where TRaven : class, TContract
            => kind == EngineStoreKind.Relational
                ? provider => provider.GetRequiredService<TRelational>()
                : provider => provider.GetRequiredService<TRaven>();

        private static void AddRelationalStores(IServiceCollection services, string databaseType)
        {
            // The module store's database: co-located when ConnectionStrings:ModuleAggregates names one, the SQLite
            // sidecar otherwise (the same resolution the storage-blind modules use, BL-153-safe).
            var (provider, connectionString) = ModuleAggregatePersistenceExtensions.ResolveModuleAggregateStore(services, databaseType);
            services.AddDbContextFactory<EngineDbContext>(options =>
            {
                if (provider is not null)
                {
                    provider.UseDatabase(options, connectionString);
                }
                else
                {
                    options.UseSqlite(connectionString);
                }
            });
            services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<EngineDbContext>>().CreateDbContext());

            // Created at first boot and caught up additively afterwards by the module store's initializer (BL-52).
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleAggregateStoreSchema, EfModuleAggregateStoreSchema<EngineDbContext>>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ModuleAggregateStoreInitializer>());

            services.TryAddScoped<EfPipelineDefinitionStore>();
            services.TryAddScoped<EfPipelineInstanceStore>();
            services.TryAddScoped<EfBookmarkStore>();
            services.TryAddScoped<EfBookmarkQueueStore>();
            services.TryAddScoped<EfTriggerStore>();
            services.TryAddScoped<EfStepExecutionStore>();
            services.TryAddScoped<EfPipelineExecutionLogStore>();
            services.TryAddScoped<EfKeyValueStore>();
        }

        // BL-232: the engine's rows and documents in an installation's backup, on either store family; BL-233: and the site
        // folders the file steps read and write.
        private static void AddBackup(IServiceCollection services)
        {
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant, EngineBackupParticipant>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant, PipelineFilesBackupParticipant>());
        }

        private static void AddRavenStores(IServiceCollection services)
        {
            // The document store itself is the host's (AddRavenFrameworkStore); the database is routed per Stomme
            // tenant exactly as RavenRepository routes it.
            services.TryAddScoped<ITenantContext, ModuleFallbackTenantContext>();
            services.TryAddSingleton<IRavenTenantDatabaseResolver, NullRavenTenantDatabaseResolver>();

            services.TryAddScoped<RavenPipelineDefinitionStore>();
            services.TryAddScoped<RavenPipelineInstanceStore>();
            services.TryAddScoped<RavenBookmarkStore>();
            services.TryAddScoped<RavenBookmarkQueueStore>();
            services.TryAddScoped<RavenTriggerStore>();
            services.TryAddScoped<RavenStepExecutionStore>();
            services.TryAddScoped<RavenPipelineExecutionLogStore>();
            services.TryAddScoped<RavenKeyValueStore>();
        }
    }
}
