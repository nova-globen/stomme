#nullable enable annotations // project is nullable-disabled; make the IRelationalDatabase?/TenantId? annotations legal (CS8632) without turning on flow analysis
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Stomme.Databases.Interfaces;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// The provider-neutrality GUARANTEE for a clean-arch (DDD/CQRS) module. A module with a
    /// <c>Guid</c>-keyed <see cref="AggregateRoot{TId}"/> persisted through the clean-arch stack
    /// (<c>IRepository&lt;TAggregate, TId&gt;</c> + <c>IUnitOfWork</c>) calls this ONCE from its
    /// <c>IServerStartup.ConfigureServices</c> and gets a working store on BOTH providers — EF Core on a
    /// relational install, the install's RavenDB document store on a document install — with no per-provider
    /// author work beyond genuinely provider-specific queries.
    /// <para>
    /// Before this, a clean-arch module authored in the FeatureManagement shape (its own EF <c>DbContext</c> +
    /// a hand-composed <c>EfRepository</c>) was EF-only by construction: the Guestbook sample ran its guestbook
    /// on SQLite even on a RavenDB install. This helper is the smallest shared thing that
    /// closes that gap, mirroring how FeatureManagement got its Raven branch — but reusable, and
    /// registering the CLOSED <c>IRepository&lt;TAggregate, TId&gt;</c> rather than the open generic.
    /// </para>
    /// </summary>
    public static class ModuleAggregatePersistenceExtensions
    {
        /// <summary>
        /// Wires <typeparamref name="TAggregate"/>'s clean-arch persistence for whichever provider the install
        /// selected, read ONCE from <c>Database:DefaultDBType</c> at composition time (the same key
        /// <c>ConfigManager.GetDatabaseType()</c> and FeatureManagement's branch read; the pre-6.1.5
        /// <c>, Stomme.Database.*</c> namespace is rewritten). This is a COMPOSITION-time branch for the same
        /// reason as the other provider-selected registrations: the provider must be persisted in config by the time the host boots, which is
        /// true for every post-install boot (the wizard restarts the host after writing <c>DefaultDBType</c>).
        /// <list type="bullet">
        ///   <item><b>Relational</b> → an <see cref="IDbContextFactory{TContext}"/> + a per-scope
        ///     <typeparamref name="TContext"/> built from it (a single <c>DbContextOptions</c> registration, the
        ///     FeatureManagement shape), a CLOSED <c>IRepository&lt;TAggregate, TId&gt;</c> bound to
        ///     <see cref="EfRepository{TAggregate, TId}"/> over <typeparamref name="TContext"/>, and an
        ///     <c>EnsureCreated</c> schema initializer (the module owns and creates its own schema; aggregates
        ///     registered after the database first exists get their missing tables created ADDITIVELY at
        ///     startup).</item>
        ///   <item><b>Document (RavenDB)</b> → a CLOSED <c>IRepository&lt;TAggregate, TId&gt;</c> bound to
        ///     <see cref="RavenRepository{TAggregate, TId}"/>, and NO <c>DbContext</c>/schema/seeder (RavenDB is
        ///     schemaless). The Raven support singletons (a lazily-built <c>IDocumentStore</c>, the change-vector
        ///     tracker, id generator and tenant-database resolver) are <c>TryAdd</c>ed by
        ///     <c>AddRavenFrameworkStore</c> in the composition root BEFORE any module startup runs.</item>
        /// </list>
        /// <b>Why the CLOSED registration.</b> FeatureManagement owns the OPEN-generic
        /// <c>IRepository&lt;,&gt;</c>; a second open-generic registration is last-wins and would silently
        /// re-point live feature flags. A CLOSED registration for exactly <c>IRepository&lt;TAggregate,
        /// TId&gt;</c> wins over the open generic in MS.DI <b>regardless of module load order</b> (verified), so
        /// any number of clean-arch modules registered through this helper coexist, each provider-neutral,
        /// without touching the open generic.
        /// </summary>
        /// <typeparam name="TAggregate">The module aggregate root (its own consistency boundary).</typeparam>
        /// <typeparam name="TId">The aggregate's identity type.</typeparam>
        /// <typeparam name="TContext">The module's own EF Core context (used only on a relational install).</typeparam>
        /// <param name="configureRelationalStore">
        /// Applies the relational provider to <typeparamref name="TContext"/> (e.g.
        /// <c>options =&gt; options.UseSqlite(connectionString)</c>). Ignored on a document install.
        /// </param>
        public static IServiceCollection AddModuleAggregatePersistence<TAggregate, TId, TContext>(
            this IServiceCollection services, Action<DbContextOptionsBuilder> configureRelationalStore)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull
            where TContext : StommeDbContext
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configureRelationalStore);

            // The shared clean-arch pieces EfRepository/RavenRepository + the unit of work need. TryAdd: the
            // host and other modules register the same types, so either order yields ONE shared scoped instance
            // per request. Tenant-scoped: the fallback is a single logical tenant, overridden by the host's real
            // ITenantContext (registered first in the composition root, so this stands down in production) — the
            // same shape AddFeatureManagementCore uses.
            services.TryAddScoped<ITenantContext, ModuleFallbackTenantContext>();
            services.TryAddScoped<IAggregateTracker, AggregateTracker>();
            services.TryAddScoped<IDomainEventDispatcher, CurrentScopeDomainEventDispatcher>();
            services.TryAddScoped<IUnitOfWork, UnitOfWork>();

            // The N-way provider registry decides which store persists the aggregate, keyed on
            // Database:DefaultDBType — no more relational?EF:Raven binary, and an unknown provider FAILS LOUD.
            // The relational provider invokes the EF wiring below (a module's OWN TContext); the document/file
            // providers ignore it and bind their generic repository.
            var databaseType = ResolveConfiguredDatabaseType(services);
            ModuleRepositoryProviderRegistry.Resolve(databaseType).Register<TAggregate, TId>(services, databaseType, () =>
            {
                // A scoped factory plus one context per scope built from it — a single DbContextOptions
                // registration, no open-generic collision with the framework/FeatureManagement store.
                services.AddDbContextFactory<TContext>(configureRelationalStore, ServiceLifetime.Scoped);
                services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<TContext>>().CreateDbContext());

                // The CLOSED repository, composed EXPLICITLY over the module's own TContext — never the ambient
                // DbContext (which is itself a last-wins registration another module might own) and never the
                // open generic. Reusing EfRepository means the aggregate enrolls in the ambient tracker on add
                // and the unit of work dispatches its domain events in the caller's scope, with no per-module
                // dispatch code.
                services.AddScoped<IRepository<TAggregate, TId>>(sp => new EfRepository<TAggregate, TId>(
                    sp.GetRequiredService<TContext>(),
                    sp.GetRequiredService<IAggregateTracker>(),
                    sp.GetRequiredService<ITenantContext>(),
                    sp.GetServices<IDomainEventSerializer>()));

                // The module owns its standalone schema, so it creates it (EnsureCreated), exactly as
                // FeatureManagement's FeatureSeeder and the old GuestbookSeeder did. The schema is registered as a
                // per-context SCHEMA HOLDER (not itself a hosted service — an OPEN-generic IHostedService would
                // be picked up by the framework's reflective hosted-service scan and fail to instantiate
                // unbound); a single non-generic initializer drains every registered holder at startup.
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IModuleAggregateStoreSchema, EfModuleAggregateStoreSchema<TContext>>());
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IHostedService, ModuleAggregateStoreInitializer>());
            });

            AddBackupParticipant<TAggregate, TId, TContext>(services);
            return services;
        }

        /// <summary>
        /// The STORAGE-BLIND overload: a <c>Guid</c>-keyed clean-arch module registers its aggregate for
        /// persistence WITHOUT authoring any EF <c>DbContext</c>, value-object converter, store or provider
        /// branch — it names only the aggregate and its id, and the HOST persists it on whichever provider the
        /// install selected. On a relational install the aggregate is mapped into the shared, host-owned
        /// <see cref="ModuleAggregatesDbContext"/> (which reflects value objects onto plain string columns by the
        /// <c>IStringValueObject</c> convention); on a RavenDB install it round-trips through the generic
        /// <see cref="RavenRepository{TAggregate, TId}"/> exactly as the 3-arg overload's Raven branch does. The
        /// CLOSED <c>IRepository&lt;TAggregate, TId&gt;</c> is registered per aggregate, so any number of
        /// storage-blind modules coexist without touching the open generic.
        /// <para>
        /// <b>Prefer this overload.</b> The 3-arg overload above (which takes a module <c>TContext</c> + an EF
        /// options lambda) remains for the special case of a module that genuinely needs its own context — e.g.
        /// FeatureManagement maps two aggregates into one context. A single-aggregate module needs neither.
        /// </para>
        /// <para>
        /// <b>Where the relational tables live.</b> A dynamic reflective model cannot carry EF migrations, so the
        /// shared store's schema is created with <c>EnsureCreated</c>, which requires the store to be a STANDALONE
        /// database (EnsureCreated is a no-op against the framework's already-created database). By default that is
        /// a SQLite sidecar beside the framework data (<c>Stomme.ModuleAggregates.db</c>) — working on every
        /// install and drivable. Set <c>ConnectionStrings:ModuleAggregates</c> to co-locate module tables on the
        /// install's own relational provider instead (then the install's <see cref="IRelationalDatabase"/> is
        /// applied to that connection). On a RavenDB install module data always lives in the install's document
        /// store, so the host fully decides storage there.
        /// </para>
        /// <para>
        /// <b>Marketplace arrivals.</b> <c>EnsureCreated</c> alone is a no-op against an EXISTING store
        /// database, so an aggregate enrolled after the database was first created (a module installed from the
        /// marketplace, then a restart) would silently have no table. The schema initializer therefore catches
        /// the schema up ADDITIVELY at startup: it probes each mapped table and creates only the missing ones
        /// (provider-correct DDL via EF's model differ), never touching existing tables or data.
        /// </para>
        /// </summary>
        /// <typeparam name="TAggregate">The module aggregate root (its own consistency boundary).</typeparam>
        /// <typeparam name="TId">The aggregate's identity type.</typeparam>
        public static IServiceCollection AddModuleAggregatePersistence<TAggregate, TId>(
            this IServiceCollection services)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull
        {
            ArgumentNullException.ThrowIfNull(services);

            // The same shared clean-arch pieces EfRepository/RavenRepository + the unit of work need, TryAdded so
            // the host and other modules yield ONE shared scoped instance per request (see the 3-arg overload).
            services.TryAddScoped<ITenantContext, ModuleFallbackTenantContext>();
            services.TryAddScoped<IAggregateTracker, AggregateTracker>();
            services.TryAddScoped<IDomainEventDispatcher, CurrentScopeDomainEventDispatcher>();
            services.TryAddScoped<IUnitOfWork, UnitOfWork>();

            // The N-way provider registry decides the store, keyed on Database:DefaultDBType — no more
            // relational?EF:Raven binary, and an unknown provider FAILS LOUD. The relational provider enrols the
            // aggregate into the shared, host-owned ModuleAggregatesDbContext and binds the closed EfRepository
            // over it; the document/file providers ignore that wiring and bind their own generic repository (both
            // already storage-blind — the generic RavenRepository/FileRepository round-trip any aggregate).
            var databaseType = ResolveConfiguredDatabaseType(services);
            ModuleRepositoryProviderRegistry.Resolve(databaseType).Register<TAggregate, TId>(services, databaseType, () =>
            {
                // Enrol the aggregate into the shared context's model, then register the shared context (once)
                // and the CLOSED repository over it. EfRepository binds to the base DbContext, so the shared
                // ModuleAggregatesDbContext resolved from the scope backs every storage-blind aggregate.
                GetOrAddModuleAggregateRegistry(services).Add(typeof(TAggregate));
                EnsureSharedModuleAggregateContext(services, databaseType);

                // TrackReads = false: a module page in an Interactive Server circuit holds ONE scope, hence one
                // context, for as long as it is open - with tracking on it kept showing the first value it read.
                services.AddScoped<IRepository<TAggregate, TId>>(sp =>
                {
                    // The shared model is built (and an unmappable aggregate quarantined) before the first
                    // repository is handed out; a quarantined aggregate gets a repository that refuses by name, and every
                    // other module on the store keeps working.
                    var context = sp.GetRequiredService<ModuleAggregatesDbContext>();
                    _ = context.Model;
                    if (sp.GetRequiredService<ModuleAggregateRegistry>().IsQuarantined(typeof(TAggregate), out var reason))
                    {
                        return new QuarantinedModuleRepository<TAggregate, TId>(reason);
                    }

                    return new EfRepository<TAggregate, TId>(
                        context,
                        sp.GetRequiredService<IAggregateTracker>(),
                        sp.GetRequiredService<ITenantContext>(),
                        sp.GetServices<IDomainEventSerializer>()) { TrackReads = false };
                });
            });

            AddBackupParticipant<TAggregate, TId, ModuleAggregatesDbContext>(services);

            // A module aggregate is TENANT-OWNED by contract - the shared store is one per
            // installation and a module-scoped key repeats across tenants, so a non-tenant-owned aggregate lets one
            // tenant read another's rows on EF and RavenDB. In-tree that is a build gate (ModuleAggregateTenancyGateTests);
            // an external module's aggregate is composed all the same and NAMED at startup (never a failure -
            // a third party must not be able to brick an install from /bin).
            // The host's OWN aggregates are the build gate's, with its reasoned list (Backup's schedule and snapshot are
            // installation-wide by design): naming them too told every stock install, at every boot, to "make it tenant-owned"
            // about the framework itself (seen in the exemplar module drive's host log).
            if (!typeof(ITenantOwned).IsAssignableFrom(typeof(TAggregate))
                && typeof(TAggregate).Assembly != typeof(ModuleAggregatePersistenceExtensions).Assembly)
            {
                services.AddSingleton(new ModuleAggregateTenancyNotice(typeof(TAggregate)));
            }

            return services;
        }
        // Every storage-blind aggregate is in the installation snapshot with ZERO module code, unless it
        // says why not with [BackupExcluded]. TryAddEnumerable: registering the same aggregate twice adds one participant.
        private static void AddBackupParticipant<TAggregate, TId, TContext>(IServiceCollection services)
            where TAggregate : AggregateRoot<TId>
            where TId : notnull
            where TContext : DbContext
        {
            if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Stomme.Backup.BackupExcludedAttribute>(typeof(TAggregate)) is null)
            {
                services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Backup.IBackupParticipant,
                    Stomme.Infrastructure.Backup.Participants.ModuleAggregateBackupParticipant<TAggregate, TId, TContext>>());
            }
        }

        // The one shared registry the host-owned ModuleAggregatesDbContext reflects over. Located off the
        // descriptor list (composition time) and created once, so every storage-blind module's registration
        // contributes its aggregate to the SAME set the shared context's model is built from.
        private static ModuleAggregateRegistry GetOrAddModuleAggregateRegistry(IServiceCollection services)
        {
            if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(ModuleAggregateRegistry))?
                    .ImplementationInstance is ModuleAggregateRegistry existing)
            {
                return existing;
            }

            var registry = new ModuleAggregateRegistry();
            services.AddSingleton(registry);
            return registry;
        }

        // Registers the shared context (a scoped factory + one context per scope, the same shape the 3-arg
        // overload and FeatureManagement use) plus its EnsureCreated schema initializer — ONCE, however many
        // storage-blind modules call the helper.
        private static void EnsureSharedModuleAggregateContext(IServiceCollection services, string databaseType)
        {
            if (services.Any(descriptor => descriptor.ServiceType == typeof(IDbContextFactory<ModuleAggregatesDbContext>)))
            {
                return;
            }

            var (relationalProvider, connectionString) = ResolveModuleAggregateStore(services, databaseType);

            services.AddDbContextFactory<ModuleAggregatesDbContext>(options =>
            {
                if (relationalProvider is not null)
                {
                    relationalProvider.UseDatabase(options, connectionString);
                }
                else
                {
                    options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(Stomme.Database.Sqlite.SqliteDatabase.BusyTimeoutSeconds(connectionString)));
                }

                // Key the model cache on the mapped aggregate set, so a second container in the same process
                // (the conformance fixtures, N modules) with a different set does not inherit the first model.
                options.ReplaceService<IModelCacheKeyFactory, ModuleAggregatesModelCacheKeyFactory>();
            }, ServiceLifetime.Scoped);
            services.AddScoped(sp =>
                sp.GetRequiredService<IDbContextFactory<ModuleAggregatesDbContext>>().CreateDbContext());

            // The module owns its standalone schema, so it creates it (EnsureCreated) — the same per-context
            // schema-holder + single non-generic initializer the 3-arg overload uses.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleAggregateStoreSchema,
                EfModuleAggregateStoreSchema<ModuleAggregatesDbContext>>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IHostedService, ModuleAggregateStoreInitializer>());
        }

        // The shared module store's provider + connection. With ConnectionStrings:ModuleAggregates set the
        // install's own relational provider is applied to that connection (co-located tables); absent, the store
        // is a standalone SQLite sidecar beside the framework data, which works on every install and is drivable.
        internal static (IRelationalDatabase? RelationalProvider, string ConnectionString) ResolveModuleAggregateStore(
            IServiceCollection services, string databaseType)
        {
            var configuration = services
                .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?
                .ImplementationInstance as IConfigurationRoot;

            var configured = configuration?.GetConnectionString("ModuleAggregates");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var resolvedType = Type.GetType(databaseType);
                var provider = resolvedType is null ? null : Activator.CreateInstance(resolvedType) as IRelationalDatabase;
                return (provider, HostDataDirectory.Expand(services, configured));
            }

            // Expands the |DataDirectory| token (and ensures the directory exists) against THIS host's content
            // root rather than the process-wide slot the last-composed host wrote (HostDataDirectory).
            return (null, HostDataDirectory.Expand(
                services, $"Data Source=|{Constants.DataDirectory}|Stomme.ModuleAggregates.db"));
        }

        /// <summary>
        /// The configured provider type, read from the <see cref="IConfigurationRoot"/> the host registers as a
        /// singleton instance, resolved off the descriptor list (composition time — the container is not built
        /// yet). Mirrors FeatureManagement's <c>ResolveConfiguredDatabaseType</c>.
        /// </summary>
        internal static string ResolveConfiguredDatabaseType(IServiceCollection services)
        {
            var configuration = services
                .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IConfigurationRoot))?
                .ImplementationInstance as IConfigurationRoot;

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
    }

    /// <summary>
    /// The fallback <see cref="ITenantContext"/> for a clean-arch module store composed without the host's real
    /// tenancy adapter (a minimal test, a background job). A single logical tenant, no system scope — the same
    /// role FeatureManagement's internal <c>DefaultTenantContext</c> plays. Registered with <c>TryAdd</c> so the
    /// host's adapter, registered first in the composition root, always wins.
    /// </summary>
    /// <summary>A module aggregate composed without <see cref="ITenantOwned"/>: named at startup, never fatal.</summary>
    public sealed record ModuleAggregateTenancyNotice(Type AggregateType);

    internal sealed class ModuleFallbackTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => TenantId.Default;

        public bool IsSystemScope => false;
    }

    /// <summary>
    /// A relational schema holder for one clean-arch module context: it knows how to bring
    /// <typeparamref name="TContext"/>'s schema up (<c>EnsureCreated</c>). Deliberately NOT an
    /// <see cref="IHostedService"/> — an open-generic hosted service is picked up by the framework's reflective
    /// hosted-service scan (which does not exclude generic type definitions) and fails to instantiate unbound.
    /// The single non-generic <see cref="ModuleAggregateStoreInitializer"/> drains every registered holder.
    /// </summary>
    internal interface IModuleAggregateStoreSchema
    {
        Task EnsureCreatedAsync(IServiceProvider scope, CancellationToken cancellationToken);
        string ContextName { get; }
    }

    internal sealed class EfModuleAggregateStoreSchema<TContext> : IModuleAggregateStoreSchema
        where TContext : DbContext
    {
        public string ContextName => typeof(TContext).Name;

        public async Task EnsureCreatedAsync(IServiceProvider scope, CancellationToken cancellationToken)
        {
            var context = scope.GetRequiredService<TContext>();
            if (await context.Database.EnsureCreatedAsync(cancellationToken))
            {
                return; // fresh database — EnsureCreated built the full current schema
            }

            // EnsureCreated is a NO-OP against an existing database, and the store database is created at
            // FIRST boot with whatever aggregates are registered THEN — so an aggregate arriving later (a module
            // installed from the marketplace, then a restart) found the database present and its table absent:
            // writes threw 'no such table', swallowed into a silent empty read (found by driving the install loop).
            // Catch the schema up ADDITIVELY: create only the missing tables, never touching existing data.
            await CreateMissingTablesAsync(context, scope, cancellationToken);

            // An aggregate that GAINED a property after its table existed (the Guestbook's tenant) found the
            // table present and the column absent - every read threw. Catch the columns up the same way: a missing
            // column is ADDED, and added NULLABLE whatever the model says, because the rows already there cannot
            // hold a value for it (a tenant-owned aggregate's legacy rows then hold no tenant, and every tenant's
            // filter hides them: fail-closed, nothing deleted).
            await AddMissingColumnsAsync(context, scope, cancellationToken);
        }

        private static async Task AddMissingColumnsAsync(
            TContext context, IServiceProvider scope, CancellationToken cancellationToken)
        {
            var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
            var relationalModel = model.GetRelationalModel();
            var sqlHelper = context.GetService<ISqlGenerationHelper>();
            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                var operations = new List<MigrationOperation>();
                var added = new List<string>();
                foreach (var table in relationalModel.Tables)
                {
                    var qualifiedTable = sqlHelper.DelimitIdentifier(table.Name, table.Schema);
                    foreach (var column in table.Columns)
                    {
                        // The same dialect-free probe the table catch-up uses, one column at a time; identifiers come
                        // from the EF model and are delimiter-quoted. A table that is itself missing was created above.
                        try
                        {
                            await using var probe = connection.CreateCommand();
                            probe.CommandText = $"SELECT {sqlHelper.DelimitIdentifier(column.Name)} FROM {qualifiedTable} WHERE 1 = 0";
                            await probe.ExecuteNonQueryAsync(cancellationToken);
                            continue;
                        }
                        catch (DbException)
                        {
                        }

                        var property = column.PropertyMappings.First().Property;
                        operations.Add(new AddColumnOperation
                        {
                            Table = table.Name,
                            Schema = table.Schema,
                            Name = column.Name,
                            ClrType = property.ClrType,
                            ColumnType = column.StoreType,
                            IsNullable = true,
                            MaxLength = property.GetMaxLength(),
                            IsUnicode = property.IsUnicode(),
                            DefaultValueSql = column.DefaultValueSql,
                        });
                        added.Add($"{table.Name}.{column.Name}");
                    }
                }

                if (operations.Count == 0)
                {
                    return;
                }

                foreach (var migrationCommand in context.GetService<IMigrationsSqlGenerator>().Generate(operations, model))
                {
                    await using var ddl = connection.CreateCommand();
                    ddl.CommandText = migrationCommand.CommandText;
                    await ddl.ExecuteNonQueryAsync(cancellationToken);
                }

                scope.GetService<ILoggerFactory>()?
                    .CreateLogger(typeof(EfModuleAggregateStoreSchema<TContext>).Name)
                    .LogInformation(
                        "Module store {Context}: added {Count} missing column(s), nullable, for aggregates that grew after their table existed: {Columns}",
                        typeof(TContext).Name, added.Count, string.Join(", ", added));
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }

        private static async Task CreateMissingTablesAsync(
            TContext context, IServiceProvider scope, CancellationToken cancellationToken)
        {
            // The design-time model is what RelationalDatabaseCreator itself builds DDL from — the runtime
            // model may have relational metadata trimmed.
            var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
            var relationalModel = model.GetRelationalModel();
            var sqlHelper = context.GetService<ISqlGenerationHelper>();

            var connection = context.Database.GetDbConnection();
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                // Probe per table rather than reading a catalog view: the store may sit on ANY relational
                // provider (the SQLite sidecar by default, the install's own provider when ModuleAggregates is
                // co-located), and a zero-row SELECT against the delimited name is the one dialect-free
                // existence check. The identifiers come from the EF model and are delimiter-quoted — no
                // external input reaches the SQL.
                var missing = new HashSet<(string Name, string Schema)>();
                foreach (var table in relationalModel.Tables)
                {
                    var qualified = sqlHelper.DelimitIdentifier(table.Name, table.Schema);
                    try
                    {
                        await using var probe = connection.CreateCommand();
                        probe.CommandText = $"SELECT 1 FROM {qualified} WHERE 1 = 0";
                        await probe.ExecuteNonQueryAsync(cancellationToken);
                    }
                    catch (DbException)
                    {
                        missing.Add((table.Name, table.Schema));
                    }
                }

                if (missing.Count == 0)
                {
                    return;
                }

                // Provider-correct DDL for exactly the missing tables: diff an empty model against the current
                // one (yields the full create set) and keep only the operations that target a missing table.
                var operations = context.GetService<IMigrationsModelDiffer>()
                    .GetDifferences(null, relationalModel)
                    .Where(operation => operation switch
                    {
                        CreateTableOperation table => missing.Contains((table.Name, table.Schema)),
                        CreateIndexOperation index => missing.Contains((index.Table, index.Schema)),
                        AddForeignKeyOperation foreignKey => missing.Contains((foreignKey.Table, foreignKey.Schema)),
                        _ => false,
                    })
                    .ToList();

                foreach (var migrationCommand in context.GetService<IMigrationsSqlGenerator>().Generate(operations, model))
                {
                    await using var ddl = connection.CreateCommand();
                    ddl.CommandText = migrationCommand.CommandText;
                    await ddl.ExecuteNonQueryAsync(cancellationToken);
                }

                scope.GetService<ILoggerFactory>()?
                    .CreateLogger(typeof(EfModuleAggregateStoreSchema<TContext>).Name)
                    .LogInformation(
                        "Module store {Context}: created {Count} missing table(s) for aggregates registered after the database existed: {Tables}",
                        typeof(TContext).Name, missing.Count, string.Join(", ", missing.Select(table => table.Name)));
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }

    /// <summary>
    /// Brings every clean-arch module's relational store up at startup by creating its schema if absent — the
    /// module owns its standalone database, so it owns creating it, exactly as FeatureManagement's
    /// <c>FeatureSeeder</c> does. NON-generic (so the framework's reflective hosted-service scan can instantiate
    /// it) and DI-resolvable; it enumerates the per-context schema holders each relational module registered. A
    /// store that cannot start must not take the host down: the failure is logged and the module simply reads an
    /// empty store.
    /// </summary>
    internal sealed class ModuleAggregateStoreInitializer(
        IServiceProvider services, ILogger<ModuleAggregateStoreInitializer> logger)
        : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var scope = services.CreateScope();
            foreach (var notice in scope.ServiceProvider.GetServices<ModuleAggregateTenancyNotice>())
            {
                logger.LogWarning(
                    "Module aggregate {Aggregate} is not tenant-owned (ITenantOwned): on a multi-tenant installation its rows are shared by every tenant of this store. Make it tenant-owned, or run this module on a single-tenant installation only.",
                    notice.AggregateType.FullName);
            }

            await EnsureAllAsync(scope.ServiceProvider, logger, cancellationToken);
        }

        /// <summary>
        /// The schema step itself - <c>EnsureCreated</c> plus the missing-table catch-up for every registered
        /// relational module store - callable OUTSIDE the hosted-service start. Measured: the boot path's
        /// <c>SiteContentReconciliationRunner</c> runs from <c>Program.Main</c>'s <c>InstallAsync</c>, before any hosted
        /// service starts, and without this it wrote to tables that did not exist yet (SQLite "no such table:
        /// NotificationTemplateAggregate", PostgreSQL 42P01). Idempotent: running it there and again at host start is
        /// harmless. A no-op on RavenDB/File (no schema holder is registered).
        /// </summary>
        internal static async Task EnsureAllAsync(IServiceProvider scoped, ILogger logger, CancellationToken cancellationToken)
        {
            // Build the shared model first, isolating what cannot be mapped, and say ONCE which aggregate is off
            // and why. Before this one unmappable aggregate failed the model every module shares, and the whole store
            // "read as empty" (measured: a healthy module's first write threw on its neighbour's property).
            if (scoped.GetService<ModuleAggregateRegistry>() is { } registry &&
                scoped.GetService<Microsoft.EntityFrameworkCore.DbContextOptions<ModuleAggregatesDbContext>>() is { } options)
            {
                ModuleAggregateModelGuard.Build(options, scoped.GetRequiredService<ITenantContext>(), registry);
                foreach (var (type, reason) in registry.Quarantined)
                {
                    if (ReportedQuarantines.TryAdd(type, 0))
                    {
                        logger.LogError(
                            "Module aggregate {Aggregate} cannot be mapped into the shared module store, so its module is switched off on this host; every other module works. {Reason}",
                            type.FullName, reason);
                    }
                }
            }

            foreach (var schema in scoped.GetServices<IModuleAggregateStoreSchema>())
            {
                try
                {
                    await schema.EnsureCreatedAsync(scoped, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unable to initialize the {Context} module store; it will read as empty.", schema.ContextName);
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // A quarantine is logged once per process, though EnsureAllAsync runs on the boot path and again at host start.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, byte> ReportedQuarantines = new();
    }
}
