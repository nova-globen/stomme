using Stomme.Persistence.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Registers the framework store's migrated aggregate clusters on the persistence seam.
    /// </summary>
    public static class FrameworkStoreServiceExtensions
    {
        /// <summary>
        /// Wires the framework store's relational (EF Core) provider. Call AFTER <c>AddStommeTenancy()</c>
        /// — the seam's <see cref="EfRepository{TAggregate, TId}"/> takes an <c>ITenantContext</c>, and the
        /// real adapter has to be in the collection before <c>AddFeatureManagement</c>'s single-tenant
        /// fallback.
        /// </summary>
        /// <remarks>
        /// Deliberately registers only CLOSED, framework-store-specific service types. The open-generic
        /// <c>IRepository&lt;,&gt;</c>, <c>DbContext</c> and <c>IDocumentSessionFactory</c> registrations are
        /// already owned by <c>AddFeatureManagement</c> (via <c>AddEfCorePersistence</c>) and point at the
        /// feature module's own SQLite store; a second open-generic registration here would be last-wins and
        /// would silently re-point live feature flags at the tenant database. Every new cluster gets its own
        /// <c>I{Aggregate}Store</c> interface for the same reason.
        /// </remarks>
        public static IServiceCollection AddStommeFrameworkStore(this IServiceCollection services)
        {
            // The seam's in-process domain-event bookkeeping. TryAdd because AddEfCorePersistence registers
            // the same implementation and either order must yield one shared scoped tracker.
            services.TryAddScoped<IAggregateTracker, AggregateTracker>();

            // Transient, matching the repositories that consume them: each store instance owns one
            // TenantDBContext resolved against the tenant current at construction. See EfSiteStore.
            services.AddTransient<IFrameworkStoreSessionFactory, FrameworkStoreSessionFactory>();
            services.AddTransient<ISiteStore, EfSiteStore>();
            services.AddTransient<IPageStore, EfPageStore>();
            services.AddTransient<IModuleStore, EfModuleStore>();

            // The Users/Roles cluster. Named IUserAccountStore/ISecurityRoleStore rather than
            // IUserStore/IRoleStore because ASP.NET Identity owns those names in this host and is still the
            // live authentication path — see the remarks on IUserAccountStore.
            services.AddTransient<IUserAccountStore, EfUserAccountStore>();
            services.AddTransient<ISecurityRoleStore, EfSecurityRoleStore>();
            services.AddTransient<IUserRoleAssignmentStore, EfUserRoleAssignmentStore>();

            // The Setting cluster. TWO stores over ONE aggregate: Setting is the only framework-store
            // table that lives in both the tenant and the master database, and SettingRepository routes
            // between them by EntityName exactly as it always has. The master pair is bound to
            // MasterDBContext, whose connection is installation-wide rather than per-tenant.
            services.AddTransient<ISettingStore, EfSettingStore>();
            services.AddTransient<IMasterSettingStore, EfMasterSettingStore>();
            services.AddTransient<IMasterStoreSessionFactory, MasterStoreSessionFactory>();

            // The Job/JobLog PAIR — the master-bookkeeping cluster. MASTER-side only, bound to
            // MasterDBContext like the master half of Setting, and read through IMasterStoreSessionFactory. No
            // tenant twin: neither table exists in a tenant database. The job scheduler resolves these from a
            // background scope with no request and no tenant, exactly as it resolved the raw MasterDBContext.
            services.AddTransient<IJobStore, EfJobStore>();
            services.AddTransient<IJobLogStore, EfJobLogStore>();

            // Theme — the master-bookkeeping cluster's last member. MASTER-side only, bound to
            // MasterDBContext and read through IMasterStoreSessionFactory, exactly like the Job/JobLog pair. No
            // tenant twin: the Theme table exists in the master database only.
            services.AddTransient<IThemeStore, EfThemeStore>();

            // ModuleDefinition — the master-bookkeeping cluster's ModuleDefinition member. MASTER-side
            // only, bound to MasterDBContext and read through IMasterStoreSessionFactory, exactly like Theme and
            // the Job/JobLog pair. No tenant twin: the ModuleDefinition table exists in the master database only.
            // Read during module discovery at startup (DatabaseManager.MigrateModulesAsync) with no tenant, which
            // the master session factory + master-bound store both serve.
            services.AddTransient<IModuleDefinitionStore, EfModuleDefinitionStore>();

            // Tenant + Alias — the routing core, and the FINAL master-bookkeeping cluster. MASTER-side only,
            // bound to MasterDBContext and read through IMasterStoreSessionFactory like Theme/ModuleDefinition and the
            // Job/JobLog pair. No tenant twin: neither table exists in a tenant database. TenantResolver/AliasAccessor
            // resolve Alias -> Site -> Tenant on every request; the installer creates the Tenant then its Aliases.
            // Registered as a pair because they land as a pair. When these land, MasterDBContext holds no content
            // DbSet — the compiler's proof that move is complete.
            services.AddTransient<ITenantStore, EfTenantStore>();
            services.AddTransient<IAliasStore, EfAliasStore>();

            // The Permission cluster. ONE store — Permission is tenant-side only, so there is no master
            // twin here and no IsMaster routing to do. This store is also what RoleRepository/UserRepository
            // take for their cascade cleanups, in place of the raw db.Permission they used to hold: a store
            // has no repository edges, so it cannot close the cycle that IPermissionRepository would
            // (PermissionRepository -> IRoleRepository). See src/Stomme.Server/README.md (persistence seam).
            services.AddTransient<IPermissionStore, EfPermissionStore>();

            // The five LEAF clusters: Language, Log, Profile, SiteTask, Notification. Tenant-side only,
            // so one store each and no master twin. Transient for the same tenant-correctness reason as every
            // store above — see EfFrameworkStore<TAggregate>.
            services.AddTransient<ILanguageStore, EfLanguageStore>();
            services.AddTransient<ILogStore, EfLogStore>();
            services.AddTransient<IProfileStore, EfProfileStore>();
            services.AddTransient<ISiteTaskStore, EfSiteTaskStore>();
            services.AddTransient<INotificationStore, EfNotificationStore>();

            // The SiteGroup PAIR. Registered together because they had to MIGRATE together — see
            // ISiteGroupStore. Tenant-side only, so no master twin, and transient for the same
            // tenant-correctness reason as every store above.
            services.AddTransient<ISiteGroupStore, EfSiteGroupStore>();
            services.AddTransient<ISiteGroupMemberStore, EfSiteGroupMemberStore>();

            // Visitor, UrlMapping and PageModule. Tenant-side only, so one store each and no master
            // twin. PageModule is the render path's own table — see IPageModuleStore.
            services.AddTransient<IVisitorStore, EfVisitorStore>();
            services.AddTransient<IUrlMappingStore, EfUrlMappingStore>();
            services.AddTransient<IPageModuleStore, EfPageModuleStore>();

            // Folder. Tenant-side only, so one store and no master twin. Note its File half did NOT
            // land with it — see IFolderStore for why, and note that File therefore keeps its DbSet on
            // TenantDBContext and needs no store registered here.
            services.AddTransient<IFolderStore, EfFolderStore>();

            // File — the last framework cluster onto the seam, and the one held back rather than
            // shipped on one provider. Its DbSet is gone from TenantDBContext; see IFileStore for how the
            // normalized lookup columns removed the provider disagreement that blocked it.
            services.AddTransient<IFileStore, EfFileStore>();

            // The SearchContent cluster — the last to cross. Four tables, tenant-side only. After this,
            // TenantDBContext maps only MigrationHistory (a permanent [Keyless] exception).
            services.AddTransient<ISearchContentStore, EfSearchContentStore>();
            services.AddTransient<ISearchContentPropertyStore, EfSearchContentPropertyStore>();
            services.AddTransient<ISearchContentWordStore, EfSearchContentWordStore>();
            services.AddTransient<ISearchWordStore, EfSearchWordStore>();

            // The search READ. Not a repository — the one closed contract on this seam that is not, because
            // the read is a prefix filter plus a runtime-keyed aggregation, which no portable LINQ expresses.
            // The EF half keeps GROUP BY + SUM in SQL. See ISearchQueryStore.
            services.AddTransient<ISearchQueryStore, EfSearchQueryStore>();

            return services;
        }
    }
}
