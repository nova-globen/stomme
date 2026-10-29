using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Raven.Client.Documents;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// How the RavenDB framework store addresses its databases and how it treats index staleness.
    /// </summary>
    public sealed class RavenFrameworkStoreOptions
    {
        /// <summary>
        /// The database holding the tenant-side framework store (Site, Page, Module, User, Role, UserRole and
        /// the tenant half of Setting). Null/empty uses the document store's own default database.
        /// </summary>
        public string FrameworkDatabase { get; set; }

        /// <summary>
        /// The database holding the MASTER half of the Setting cluster — Host, Job, ModuleDefinition, Theme
        /// and <c>master:*</c> settings. Null/empty uses the document store's own default database,
        /// which collapses the two databases (framework and master) into one; set it to keep the master store separate, as the
        /// relational path keeps <see cref="MasterDBContext"/> separate from <see cref="TenantDBContext"/>.
        /// </summary>
        public string MasterDatabase { get; set; }

        /// <summary>
        /// Whether every framework-store read waits for RavenDB's indexes to catch up with the writes that
        /// precede it (<c>WaitForNonStaleResults</c> on query, <c>WaitForIndexesAfterSaveChanges</c> on save).
        /// <para>
        /// <b>Defaults to true, and the default is the conservative one on purpose.</b> RavenDB indexes
        /// asynchronously, so a query issued after a write may silently miss it — and EVERY framework-store
        /// read is a query, never a load by id. The failure mode is not an error: it is an empty result
        /// that callers read as "absent", which is how a fresh install locks its own host user out
        /// (<c>DatabaseManager</c> resolves the Host role by query and a miss means no Host role) and how an
        /// authenticated user silently loses every role (<c>UserRoleRepository.Hydrate</c> drops an assignment
        /// whose Role it cannot find). Off is faster; off is also wrong in ways nothing reports. See
        /// src/Stomme.Persistence.RavenDb/README.md.
        /// </para>
        /// <para>
        /// <b>What this is NOT.</b> It is not the per-site, install-only policy the hazard really wants —
        /// that needs the seam to know it is installing, and where that flag lives is an open
        /// question (src/Stomme.Persistence.RavenDb/README.md). This is the blanket switch, defaulted safe, left where an
        /// operator can see and relax it. The Raven framework store is wired into the host (resolve-time
        /// selection keyed on <c>GetDatabaseType()</c>) and RavenDB is install-selectable, so this default
        /// DOES apply to a live path on a RavenDB install. It is deliberately left ON (correctness over speed): see the
        /// role-hydration hazard above.
        /// </para>
        /// </summary>
        public bool WaitForNonStaleResults { get; set; } = true;
    }

    /// <summary>
    /// Wires the framework store's RavenDB provider — the second implementation of the same closed store
    /// interfaces the EF Core provider implements. The set is whatever <c>ISiteStore.cs</c>
    /// declares, and <c>RavenFrameworkStoreRegistrationTests</c> discovers it reflectively rather than
    /// counting it here and the remarks on <see cref="RavenSiteStore"/> for why no count is written
    /// down.
    /// </summary>
    public static class RavenFrameworkStoreServiceExtensions
    {
        /// <summary>
        /// Registers the RavenDB framework stores and the two session factories (framework and master). Call INSTEAD OF
        /// <see cref="FrameworkStoreServiceExtensions.AddStommeFrameworkStore"/>, and after
        /// <c>AddStommeTenancy()</c> — <see cref="RavenRepository{TAggregate, TId}"/> takes an
        /// <c>ITenantContext</c> and filters visibility by it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Deliberately NOT <c>AddRavenPersistence</c>, and this is the sharp edge.</b> That extension ends
        /// with <c>services.AddScoped(typeof(IRepository&lt;,&gt;), typeof(RavenRepository&lt;,&gt;))</c> — an
        /// OPEN-GENERIC registration. <c>AddFeatureManagement</c> already owns <c>IRepository&lt;,&gt;</c> in
        /// this host (via <c>AddEfCorePersistence&lt;FeatureManagementDbContext&gt;</c>) and points it at the
        /// feature module's own SQLite store. DI is last-wins, so calling <c>AddRavenPersistence</c> here would
        /// silently re-point every live feature flag at RavenDB — no error, no warning, just flags read from a
        /// store that has never held one. Everything below registers CLOSED types only; nothing here may ever
        /// register <c>IRepository&lt;,&gt;</c>, <c>IDocumentSessionFactory</c> or <c>DbContext</c>. See the remarks on <see cref="ISiteStore"/>.
        /// </para>
        /// <para>
        /// The ambient <see cref="IDocumentSessionFactory"/> is likewise left alone: the framework store reads
        /// through its OWN two factories (<see cref="IFrameworkStoreSessionFactory"/> and
        /// <see cref="IMasterStoreSessionFactory"/>), which are closed interfaces for exactly this reason.
        /// </para>
        /// </remarks>
        public static IServiceCollection AddRavenFrameworkStore(
            this IServiceCollection services,
            IDocumentStore store,
            Action<RavenFrameworkStoreOptions> configure = null)
        {
            ArgumentNullException.ThrowIfNull(store);

            // An already-built store: capture it. This overload is what the conformance suite and the
            // registration gate use; the host uses the lazy factory overload below.
            return services.AddRavenFrameworkStore(_ => store, configure);
        }

        /// <summary>
        /// The LAZY overload: the <see cref="IDocumentStore"/> is built by <paramref name="storeFactory"/>
        /// on FIRST resolve, not at registration time. This is what lets the host wire the RavenDB provider
        /// unconditionally alongside the EF one: on a relational install no Raven store is ever resolved, so the
        /// factory is never invoked and no connection to RavenDB is attempted (the store's Urls/Database live in
        /// the persisted connection string, which is written mid-install after the container is already built —
        /// there is nothing to build the store from at composition time on a fresh boot). The provider actually
        /// resolved for each closed store interface is chosen at first resolve by
        /// <c>SelectFrameworkStoreProviderByConfig</c>, keyed on <c>IConfigManager.GetDatabaseType()</c>.
        /// </summary>
        public static IServiceCollection AddRavenFrameworkStore(
            this IServiceCollection services,
            Func<IServiceProvider, IDocumentStore> storeFactory,
            Action<RavenFrameworkStoreOptions> configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(storeFactory);

            var options = new RavenFrameworkStoreOptions();
            configure?.Invoke(options);
            services.AddSingleton(options);

            // The document store is a long-lived, thread-safe connection factory; one per host. Built lazily:
            // TryAddSingleton over a FACTORY defers construction (and therefore the RavenDB connection) to the
            // first time an IDocumentStore is actually resolved — which only happens when a Raven store is
            // selected. See the summary above.
            services.TryAddSingleton(storeFactory);

            // Database-assigned identities for the aggregates the framework store inserts with an unset id.
            // Singleton: each HiLo generator caches a server-reserved range (see RavenHiLoIdGenerator). It
            // resolves the (lazy) IDocumentStore, so it too is built no earlier than first use.
            services.TryAddSingleton<IRavenIdGenerator>(sp => new RavenHiLoIdGenerator(sp.GetRequiredService<IDocumentStore>()));

            // Shared-database isolation by default: the tenant query filter separates tenants. A deployment
            // wanting database-per-tenant registers ConventionRavenTenantDatabaseResolver before this call.
            services.TryAddSingleton<IRavenTenantDatabaseResolver, NullRavenTenantDatabaseResolver>();

            // Per-scope change-vector cache: what gives the per-operation sessions optimistic concurrency.
            services.TryAddScoped<RavenChangeVectorTracker>();

            // The seam's in-process domain-event bookkeeping. TryAdd because AddEfCorePersistence registers
            // the same implementation and either order must yield one shared scoped tracker.
            services.TryAddScoped<IAggregateTracker, AggregateTracker>();

            // Transient, matching both the EF stores and the repositories that consume them.
            services.AddTransient<IFrameworkStoreSessionFactory, RavenFrameworkStoreSessionFactory>();
            services.AddTransient<ISiteStore, RavenSiteStore>();
            services.AddTransient<IPageStore, RavenPageStore>();
            services.AddTransient<IModuleStore, RavenModuleStore>();

            // The Users/Roles cluster.
            services.AddTransient<IUserAccountStore, RavenUserAccountStore>();
            services.AddTransient<ISecurityRoleStore, RavenSecurityRoleStore>();
            services.AddTransient<IUserRoleAssignmentStore, RavenUserRoleAssignmentStore>();

            // The Setting cluster: TWO stores and TWO session factories over ONE aggregate, because
            // Setting is the one framework-store table that lives in both the tenant and the master database.
            services.AddTransient<ISettingStore, RavenSettingStore>();
            services.AddTransient<IMasterSettingStore, RavenMasterSettingStore>();
            services.AddTransient<IMasterStoreSessionFactory, RavenMasterStoreSessionFactory>();

            // The Job/JobLog PAIR — the master-bookkeeping cluster. MASTER-side only, pinned to the master
            // database with FixedRavenDatabaseResolver exactly as RavenMasterSettingStore is, because Job is
            // installation-wide and the background scheduler runs with no tenant. The reflective registration gate demands
            // both twins the moment IJobStore/IJobLogStore exist.
            services.AddTransient<IJobStore, RavenJobStore>();
            services.AddTransient<IJobLogStore, RavenJobLogStore>();

            // Theme — the master-bookkeeping cluster's last member. MASTER-side only, pinned to the master
            // database with FixedRavenDatabaseResolver inside RavenThemeStore, exactly as the Job/JobLog pair is,
            // because Theme is installation-wide. The reflective registration gate demands this twin the moment IThemeStore
            // exists.
            services.AddTransient<IThemeStore, RavenThemeStore>();

            // ModuleDefinition — installation-wide registry of installed module types. MASTER-side only,
            // pinned to the master database with FixedRavenDatabaseResolver inside RavenModuleDefinitionStore,
            // exactly as Theme is, because ModuleDefinition is installation-wide and module discovery runs with no
            // tenant. The reflective registration gate demands this twin the moment IModuleDefinitionStore exists.
            services.AddTransient<IModuleDefinitionStore, RavenModuleDefinitionStore>();

            // Tenant + Alias — the routing core, and the FINAL master-bookkeeping cluster. MASTER-side only,
            // pinned to the master database with FixedRavenDatabaseResolver inside RavenTenantStore/RavenAliasStore,
            // exactly as Theme/ModuleDefinition and the Job/JobLog pair are, because both are installation-wide and
            // resolution reads them BEFORE a tenant is known. The reflective registration gate demands both twins the moment
            // ITenantStore/IAliasStore exist.
            services.AddTransient<ITenantStore, RavenTenantStore>();
            services.AddTransient<IAliasStore, RavenAliasStore>();

            // The Permission cluster, twinned. ONE store — Permission is tenant-side only, so no
            // master twin and no IsMaster routing, exactly as on the EF side. It was once missing here: the
            // cluster was migrated after this extension was written, and an enumerated registration test
            // could not notice an interface it did not list. Permission gates every render and every admin
            // action, so its absence took the whole RavenDB path down at the first PermissionRepository.
            services.AddTransient<IPermissionStore, RavenPermissionStore>();

            // The five LEAF clusters: Language, Log, Profile, SiteTask, Notification. Tenant-side only,
            // so no master twins and no IsMaster routing, exactly as on the EF side. Each is added together
            // with its EF counterpart rather than after it — which is the point of the reflective
            // registration gate: adding the interface to ISiteStore.cs makes this registration mandatory on the next run,
            // so the Permission gap (an EF-only store that took the whole RavenDB path down) cannot recur.
            services.AddTransient<ILanguageStore, RavenLanguageStore>();
            services.AddTransient<ILogStore, RavenLogStore>();
            services.AddTransient<IProfileStore, RavenProfileStore>();
            services.AddTransient<ISiteTaskStore, RavenSiteTaskStore>();
            services.AddTransient<INotificationStore, RavenNotificationStore>();

            // The SiteGroup PAIR, added in the same increment as their EF counterparts for the reason
            // above. See the remarks on RavenSiteGroupStore for the one divergence that is not a registration
            // concern: the relational unique index on (site_id, site_group_id) has no document-store
            // equivalent.
            services.AddTransient<ISiteGroupStore, RavenSiteGroupStore>();
            services.AddTransient<ISiteGroupMemberStore, RavenSiteGroupMemberStore>();

            // Visitor, UrlMapping and PageModule, added in the same increment as their EF counterparts
            // for the reason above. See the remarks on RavenUrlMappingStore for the divergence that is not a
            // registration concern: the relational unique index on (site_id, url) is the concurrency control
            // this cluster's production code CATCHES, and it has no document-store equivalent.
            services.AddTransient<IVisitorStore, RavenVisitorStore>();
            services.AddTransient<IUrlMappingStore, RavenUrlMappingStore>();
            services.AddTransient<IPageModuleStore, RavenPageModuleStore>();

            // Folder, added in the same increment as its EF counterpart for the reason above. See the
            // remarks on RavenFolderStore for the two divergences that are not registration concerns: the
            // relational unique index on (site_id, path), and RavenDB's case-INSENSITIVE string equality.
            services.AddTransient<IFolderStore, RavenFolderStore>();

            // File, added together with its EF counterpart because the reflective registration gate
            // demands both the moment IFileStore exists. See RavenFileStore for the one inherited divergence
            // (the relational UNIQUE index on (folder_id, name)) and for why RavenDB's case-insensitive string
            // equality is inert here rather than dangerous.
            services.AddTransient<IFileStore, RavenFileStore>();

            // The SearchContent cluster — Phase 3's last. Four tables, tenant-side only.
            services.AddTransient<ISearchContentStore, RavenSearchContentStore>();
            services.AddTransient<ISearchContentPropertyStore, RavenSearchContentPropertyStore>();
            services.AddTransient<ISearchContentWordStore, RavenSearchContentWordStore>();
            services.AddTransient<ISearchWordStore, RavenSearchWordStore>();

            // The search READ, by a deliberately different mechanism from the EF half: a query-time FACET
            // (AggregateBy/SumOn) over SearchContentWordIndex, which aggregates over the MATCHED set. Same
            // numbers, and every filter arm plus the sum stays in the store. A map-reduce index cannot do this
            // (its reduce key is fixed at definition time) and a full-text index is not the same relation as
            // StartsWith. See ISearchQueryStore and RavenSearchQueryStore.
            services.AddTransient<ISearchQueryStore, RavenSearchQueryStore>();

            return services;
        }

        /// <summary>
        /// Creates the databases <see cref="AddRavenFrameworkStore"/> addresses, if they do not already exist.
        /// Idempotent. The RavenDB counterpart of the relational path's <c>Database.Migrate()</c> — minus the
        /// schema, which a document store does not have (see <see cref="RavenDatabaseBootstrap"/> for why this
        /// deploys no indexes).
        /// </summary>
        public static async Task BootstrapRavenFrameworkStoreAsync(
            IDocumentStore store,
            RavenFrameworkStoreOptions options,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(options);

            await RavenDatabaseBootstrap.EnsureDatabaseAsync(store, options.FrameworkDatabase, ct);

            // The second (master) database. Skipped when it resolves to the same database as the framework store,
            // which is the supported single-database shape (the relational install is the same: the master
            // database is also a tenant database in every supported install — see FrameworkStoreModel).
            if (!SameDatabase(store, options.FrameworkDatabase, options.MasterDatabase))
            {
                await RavenDatabaseBootstrap.EnsureDatabaseAsync(store, options.MasterDatabase, ct);
            }

            // The search read's static index. It lives on the FRAMEWORK database — the tenant side —
            // because that is where the four SearchContent collections live; there is no master half.
            //
            // This is the seam's FIRST static index, and it is deployed here rather than left to RavenDB's
            // dynamic-query auto-indexing for a reason it cannot satisfy: the index LoadDocuments across
            // SearchWordAggregate and SearchContentAggregate to bring the word and the site/entity/date
            // columns onto one entry, and a dynamic query cannot LoadDocument. Without it the site arm — an
            // EF JOIN today — would have to run in memory, which the seam forbids.
            await RavenClusterCommit.EnsureIndexAsync(
                store,
                new SearchContentWordIndex(),
                string.IsNullOrEmpty(options.FrameworkDatabase) ? store.Database : options.FrameworkDatabase,
                ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Makes a TENANT's own database ready: created if missing, with the search read's static index - what
        /// <see cref="BootstrapRavenFrameworkStoreAsync"/> does for the installation's database. Idempotent.
        /// </summary>
        public static async Task EnsureTenantDatabaseAsync(IDocumentStore store, string database, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(store);
            ArgumentException.ThrowIfNullOrWhiteSpace(database);
            await RavenDatabaseBootstrap.EnsureDatabaseAsync(store, database, ct);
            await RavenClusterCommit.EnsureIndexAsync(store, new SearchContentWordIndex(), database, ct).ConfigureAwait(false);
        }

        private static bool SameDatabase(IDocumentStore store, string first, string second)
        {
            var left = string.IsNullOrEmpty(first) ? store.Database : first;
            var right = string.IsNullOrEmpty(second) ? store.Database : second;
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
