using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The framework-store conformance suite against the RavenDB provider, live against a real server
/// (RAVENDB_TEST_URL, default http://127.0.0.1:7000).
/// <para>
/// <b>Everything under test is resolved from the container through <c>AddRavenFrameworkStore</c> — the real
/// registration, not a hand-built object graph.</b> That is deliberate: the extension IS half the deliverable
/// (it is where the registration rule is either honoured or violated), and a fixture that news up the stores directly
/// would leave the registration itself unproven. It also keeps the internal store classes internal.
/// </para>
/// <para>
/// <b>Two databases,.</b> The framework store and the master store get separate RavenDB databases,
/// because that is what the Setting cluster's routing means. Binding them to one database is the shape
/// <c>The_master_and_tenant_setting_stores_are_separate_databases</c> exists to reject.
/// </para>
/// </summary>
public sealed class RavenFrameworkStoreConformanceTests : FrameworkStoreConformanceTests
{
    private static string Url => Environment.GetEnvironmentVariable("RAVENDB_TEST_URL") ?? "http://127.0.0.1:7000";

    private readonly string _frameworkDatabase = "stomme-fx-" + Guid.NewGuid().ToString("N");
    private readonly string _masterDatabase = "stomme-master-" + Guid.NewGuid().ToString("N");
    private DocumentStore _store = default!;
    private ServiceProvider _services = default!;

    protected override IFrameworkStoreSessionFactory FrameworkSessions =>
        _services.GetRequiredService<IFrameworkStoreSessionFactory>();

    protected override IMasterStoreSessionFactory MasterSessions =>
        _services.GetRequiredService<IMasterStoreSessionFactory>();

    /// <summary>
    /// False — RavenDB's string equality is case-INSENSITIVE by default. MEASURED, not read in a doc: it is
    /// what <see cref="The_seam_cannot_express_a_case_insensitive_column_filter_portably"/> asserts, and it is
    /// half of the divergence that kept the File cluster off the seam.
    /// </summary>
    protected override bool ProviderStringEqualityIsOrdinal => false;

    /// <summary>
    /// False — RavenDB's prefix match is case-INSENSITIVE, like its <c>==</c>. MEASURED: <c>StartsWith("RUN")</c>
    /// returns the lowercase-stored rows. It agrees with the SQLite fixture here and would disagree with
    /// PostgreSQL; see the remarks on the base property.
    /// </summary>
    protected override bool ProviderPrefixFilterIsOrdinal => false;

    public override async Task InitializeAsync()
    {
        _store = (DocumentStore)new DocumentStore
        {
            Urls = [Url],
            // The framework database is the store default; the master database is addressed explicitly, which
            // is what proves the session factory's database map is actually load-bearing.
            Database = _frameworkDatabase
        }.InitializeStommeStore();

        // The document provider has no change tracker to ask "is this an insert?", so audit stamps come from
        // a store-level listener.
        _store.StampAudit(new SystemAuditContext("conformance-user"));

        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new SystemScopeTenantContext());
        services.AddRavenFrameworkStore(_store, options =>
        {
            options.FrameworkDatabase = _frameworkDatabase;
            options.MasterDatabase = _masterDatabase;
            // The install/seed path's setting, and this suite's shape: read-after-write throughout. See
            // src/Stomme.Persistence.RavenDb/README.md for why this is the default and where the real hazard lives.
            options.WaitForNonStaleResults = true;
        });
        _services = services.BuildServiceProvider();

        // The bootstrap under test: nothing in the provider created these databases before this work.
        await RavenFrameworkStoreServiceExtensions.BootstrapRavenFrameworkStoreAsync(
            _store,
            _services.GetRequiredService<RavenFrameworkStoreOptions>());
    }

    public override async Task DisposeAsync()
    {
        // Drop the throwaway databases FIRST, while _store is still alive. Disposing _services beforehand tore
        // down the registered store, so the delete below threw into the empty catch and BOTH databases orphaned
        // every run — measured at 636 leaked (318 fx + 318 master) across this session's conformance runs.
        foreach (var database in new[] { _frameworkDatabase, _masterDatabase })
        {
            try
            {
                await _store.Maintenance.Server.SendAsync(new DeleteDatabasesOperation(database, hardDelete: true));
            }
            catch
            {
                // best-effort cleanup
            }
        }

        await _services.DisposeAsync();
        _store.Dispose();
    }

    // Each call resolves a fresh store from a fresh scope: an independent change-vector tracker, so two
    // stores can conflict with each other exactly as two requests would.
    protected override ISiteStore NewSites() => Resolve<ISiteStore>();

    protected override IPageStore NewPages() => Resolve<IPageStore>();

    protected override IModuleStore NewModules() => Resolve<IModuleStore>();

    protected override IUserAccountStore NewUsers() => Resolve<IUserAccountStore>();

    protected override ISecurityRoleStore NewRoles() => Resolve<ISecurityRoleStore>();

    protected override IUserRoleAssignmentStore NewUserRoles() => Resolve<IUserRoleAssignmentStore>();

    protected override ISettingStore NewSettings() => Resolve<ISettingStore>();

    protected override IMasterSettingStore NewMasterSettings() => Resolve<IMasterSettingStore>();

    // The Job/JobLog pair — master-side only, pinned to the master RavenDB database by
    // FixedRavenDatabaseResolver inside RavenJobStore/RavenJobLogStore. Resolved from the container like every
    // other store, which is what proves AddRavenFrameworkStore registered them (the reflective gate demands it).
    protected override IJobStore NewJobs() => Resolve<IJobStore>();

    protected override IJobLogStore NewJobLogs() => Resolve<IJobLogStore>();

    // The Theme cluster — master-side only, pinned to the master RavenDB database by
    // FixedRavenDatabaseResolver inside RavenThemeStore. Resolved from the container like every other store,
    // which is what proves AddRavenFrameworkStore registered it (the reflective gate demands it).
    protected override IThemeStore NewThemes() => Resolve<IThemeStore>();

    // The ModuleDefinition cluster — master-side only, pinned to the master RavenDB database by
    // FixedRavenDatabaseResolver inside RavenModuleDefinitionStore. Resolved from the container like every other
    // store, which is what proves AddRavenFrameworkStore registered it (the reflective gate demands it).
    protected override IModuleDefinitionStore NewModuleDefinitions() => Resolve<IModuleDefinitionStore>();

    // The Tenant/Alias routing pair — master-side only, pinned to the master RavenDB database by
    // FixedRavenDatabaseResolver inside RavenTenantStore/RavenAliasStore. Resolved from the container like every
    // other store, which is what proves AddRavenFrameworkStore registered them (the reflective gate demands it).
    protected override ITenantStore NewTenants() => Resolve<ITenantStore>();

    protected override IAliasStore NewAliases() => Resolve<IAliasStore>();

    // The Permission cluster, twinned. Resolving it from the container is the discriminating
    // check for this whole increment: before RavenPermissionStore existed this threw
    // InvalidOperationException("No service for type 'IPermissionStore' has been registered") — which is
    // exactly what a host on the RavenDB path hit at its first PermissionRepository construction.
    protected override IPermissionStore NewPermissions() => Resolve<IPermissionStore>();

    // The five leaf clusters, resolved from the container like every store above — which is what proves
    // AddRavenFrameworkStore actually registered them. Twinned in the same increment as their EF
    // counterparts, so unlike Permission these never spent a release EF-only.
    protected override ILanguageStore NewLanguages() => Resolve<ILanguageStore>();

    protected override ILogStore NewLogs() => Resolve<ILogStore>();

    protected override IProfileStore NewProfiles() => Resolve<IProfileStore>();

    protected override ISiteTaskStore NewSiteTasks() => Resolve<ISiteTaskStore>();

    protected override INotificationStore NewNotifications() => Resolve<INotificationStore>();

    // The SiteGroup pair, likewise twinned in the same increment as their EF counterparts. The pair is
    // the first on the seam whose EF model had a NAVIGATION between the two clusters; severing it cost this
    // provider nothing (a document store references by id anyway), but note what the RavenDB side does NOT
    // reproduce: SiteGroupMember's relational UNIQUE index on (SiteId, SiteGroupId) has no document-store
    // equivalent, so a duplicate membership is rejected on the EF path and accepted here. See src/Stomme.Persistence.RavenDb/README.md.
    protected override ISiteGroupStore NewSiteGroups() => Resolve<ISiteGroupStore>();

    protected override ISiteGroupMemberStore NewSiteGroupMembers() => Resolve<ISiteGroupMemberStore>();

    // Visitor, UrlMapping and PageModule, twinned in the same increment as their EF counterparts. Two
    // divergences these three inherit are worth naming and neither is reproducible here: UrlMapping's
    // relational UNIQUE index on (SiteId, Url) — which UrlMappingRepository's try/catch actually DEPENDS on as
    // its race control — has no document-store equivalent, and PageModule's IDeletable stamp is applied by the
    // relational path only (AuditStamp does not port the soft-delete rule). See src/Stomme.Persistence.RavenDb/README.md.
    protected override IVisitorStore NewVisitors() => Resolve<IVisitorStore>();

    protected override IUrlMappingStore NewUrlMappings() => Resolve<IUrlMappingStore>();

    protected override IPageModuleStore NewPageModules() => Resolve<IPageModuleStore>();

    // Folder. No master twin — Folder is tenant-side only. There is deliberately no File store to
    // resolve: the File cluster is NOT on the seam, and this fixture is where the reason is measured rather
    // than asserted — see The_seam_cannot_express_a_case_insensitive_column_filter_portably.
    protected override IFolderStore NewFolders() => Resolve<IFolderStore>();

    protected override IFileStore NewFiles() => Resolve<IFileStore>();

    // The SearchContent cluster. Resolved from the container like every other store — which is what
    // proves AddRavenFrameworkStore actually registers them, not just that the classes compile.
    protected override ISearchContentStore NewSearchContents() => Resolve<ISearchContentStore>();

    protected override ISearchContentPropertyStore NewSearchContentProperties() => Resolve<ISearchContentPropertyStore>();

    protected override ISearchContentWordStore NewSearchContentWords() => Resolve<ISearchContentWordStore>();

    protected override ISearchWordStore NewSearchWords() => Resolve<ISearchWordStore>();

    /// <summary>
    /// The RavenDB search read — the FACET half. It needs <c>SearchContentWordIndex</c> deployed, which
    /// <c>BootstrapRavenFrameworkStoreAsync</c> does in <see cref="InitializeAsync"/>: the real bootstrap, not
    /// a hand-rolled one, so a fixture that forgot the index would be a failing test rather than a green
    /// suite over an absent artifact.
    /// </summary>
    protected override ISearchQueryStore NewSearchQueries() => Resolve<ISearchQueryStore>();

    private T Resolve<T>() where T : notnull => _services.CreateScope().ServiceProvider.GetRequiredService<T>();

    private sealed class SystemScopeTenantContext : ITenantContext
    {
        public Tenancy.Domain.TenantId? CurrentTenantId => null;

        public bool IsSystemScope => true;
    }
}
