using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Xunit;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The FRAMEWORK STORE's contract — the migrated clusters (Sites, Pages, Modules, Users/Roles, Settings,
/// Permissions) — expressed once and run against EVERY provider. Where
/// <see cref="PersistenceConformanceTests"/> proves the seam's primitives on a synthetic aggregate, this
/// proves the thing the product actually stores, through the CLOSED store interfaces the repositories
/// consume and the two session factories they read through.
/// <para>
/// <b>The cluster set here is hand-maintained, and that is a known limit.</b> The header used to say
/// "the five migrated clusters" while Permission had no coverage on either provider — the same
/// staleness that let the RavenDB twin go missing. It is NOT fixed the same way: the registration gate could
/// go reflective because a registration is uniform (a service type either is in the collection or is not),
/// but a conformance test is a HAND-WRITTEN query copied from a named repository read, and no reflection can
/// author one. Reflection could assert that every closed store interface has an abstract factory member
/// here, which would catch a cluster added with no fixture — but it could not distinguish a cluster with real
/// query coverage from one with a factory and nothing else, so it would gate the cheap half and certify the
/// expensive half untested. The gate that actually holds is the registration one, which is reflective and
/// cannot be satisfied without a working store on both providers.
/// </para>
/// <para>
/// <b>Every query below is copied from a real repository, deliberately.</b> A conformance test that invents
/// its own query shape proves the provider can serve a query nobody issues. Each test names the repository
/// and the line it mirrors; if a shape here stops working, a named product read is broken.
/// </para>
/// </summary>
public abstract class FrameworkStoreConformanceTests : IAsyncLifetime
{
    // --- what each provider's fixture supplies ---

    /// <summary>
    /// A store bound to a fresh unit of work, as a new request would get. Factory methods rather than
    /// properties because the EF provider's stores own a DbContext (and therefore an identity map and a
    /// change tracker) per instance, exactly as EfSiteStore does.
    /// </summary>
    protected abstract ISiteStore NewSites();

    protected abstract IPageStore NewPages();

    protected abstract IModuleStore NewModules();

    protected abstract IUserAccountStore NewUsers();

    protected abstract ISecurityRoleStore NewRoles();

    protected abstract IUserRoleAssignmentStore NewUserRoles();

    protected abstract ISettingStore NewSettings();

    protected abstract IMasterSettingStore NewMasterSettings();

    // The Job/JobLog PAIR — the master-bookkeeping cluster. MASTER-side only, so both are bound to the
    // master database (the EF fixture's master connection, a separate RavenDB database), NOT the tenant side,
    // and their reads go through MasterSessions rather than FrameworkSessions.
    protected abstract IJobStore NewJobs();

    protected abstract IJobLogStore NewJobLogs();

    // The Theme cluster — the master-bookkeeping cluster's last member. MASTER-side only, so it is bound to
    // the master database (the EF fixture's master connection, a separate RavenDB database), NOT the tenant side,
    // and its reads go through MasterSessions rather than FrameworkSessions.
    protected abstract IThemeStore NewThemes();

    // The ModuleDefinition cluster — installation-wide registry of installed module types. MASTER-side only,
    // like Theme and the Job/JobLog pair, so it is bound to the master database (the EF fixture's master
    // connection, a separate RavenDB database) and its reads go through MasterSessions rather than FrameworkSessions.
    protected abstract IModuleDefinitionStore NewModuleDefinitions();

    // The Tenant/Alias routing PAIR — the master-bookkeeping wave's final cluster and the routing core.
    // MASTER-side only, like Theme/ModuleDefinition and the Job/JobLog pair, so both are bound to the master database
    // (the EF fixture's master connection, a separate RavenDB database) and their reads go through MasterSessions
    // rather than FrameworkSessions.
    protected abstract ITenantStore NewTenants();

    protected abstract IAliasStore NewAliases();

    /// <summary>The Permission cluster. ONE store — Permission is tenant-side only, so there is no master twin.</summary>
    protected abstract IPermissionStore NewPermissions();

    // The five LEAF clusters. All tenant-side only, so none has a master twin.

    protected abstract ILanguageStore NewLanguages();

    protected abstract ILogStore NewLogs();

    protected abstract IProfileStore NewProfiles();

    protected abstract ISiteTaskStore NewSiteTasks();

    protected abstract INotificationStore NewNotifications();

    // The SiteGroup pair. Two stores, and they are the first pair on the seam that USED to be joined by a
    // mapped navigation — SiteGroupMemberRepository now stitches the group on by hand from a second query, so
    // "can this provider serve both reads" is a product question, not a hypothetical.
    protected abstract ISiteGroupStore NewSiteGroups();

    protected abstract ISiteGroupMemberStore NewSiteGroupMembers();

    // Visitor, UrlMapping and PageModule. All tenant-side only, so none has a master twin. PageModule is
    // the render path's own table — every page render enumerates its page's module instances — so "can this
    // provider serve that read" is the most load-bearing question in this file.
    protected abstract IVisitorStore NewVisitors();

    protected abstract IUrlMappingStore NewUrlMappings();

    protected abstract IPageModuleStore NewPageModules();

    /// <summary>
    /// Folder. Tenant-side only, so no master twin. Its File half landed one increment later — see
    /// <see cref="The_seam_cannot_express_a_case_insensitive_column_filter_portably"/> for the measurement
    /// that delayed it and <see cref="A_normalized_column_filter_is_identical_on_every_provider"/> for the one
    /// that unblocked it. Both run against both providers here.
    /// </summary>
    protected abstract IFolderStore NewFolders();

    /// <summary>File. Tenant-side only, so no master twin.</summary>
    protected abstract IFileStore NewFiles();

    // The SearchContent cluster — Phase 3's LAST. Four tables, tenant-side only, so no master twin.

    protected abstract ISearchContentStore NewSearchContents();

    protected abstract ISearchContentPropertyStore NewSearchContentProperties();

    protected abstract ISearchContentWordStore NewSearchContentWords();

    protected abstract ISearchWordStore NewSearchWords();

    /// <summary>
    /// The SEARCH READ — the seam's one closed contract that is NOT a repository, because the read is a
    /// prefix filter plus an aggregation keyed at runtime and no portable LINQ expresses it. The two
    /// implementations are deliberately unalike (EF: <c>GROUP BY</c>+<c>SUM</c> in SQL; RavenDB: a query-time
    /// FACET over a static index), which makes "do they agree" a real question rather than a formality — and
    /// this suite is the only place it gets asked.
    /// </summary>
    protected abstract ISearchQueryStore NewSearchQueries();

    /// <summary>The tenant-side read path — what every repository but SettingRepository's master half reads through.</summary>
    protected abstract IFrameworkStoreSessionFactory FrameworkSessions { get; }

    /// <summary>The MASTER read path. A SECOND database, not a second table.</summary>
    protected abstract IMasterStoreSessionFactory MasterSessions { get; }

    public abstract Task InitializeAsync();

    public abstract Task DisposeAsync();

    // --- Sites ---

    /// <summary>
    /// SiteRepository.GetSites — <c>Query&lt;SiteAggregate&gt;().OrderBy(item =&gt; item.Name)</c>. The whole
    /// cluster's only query, issued on effectively every request and cached per tenant.
    /// </summary>
    [Fact]
    public async Task Sites_are_listed_in_name_order()
    {
        await NewSites().AddAsync(NewSite("zulu"));
        await NewSites().AddAsync(NewSite("alpha"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var names = session.Query<SiteAggregate>().OrderBy(item => item.Name).ToList().ConvertAll(item => item.Name);

        Assert.Equal(["alpha", "zulu"], names);
    }

    // --- Pages ---

    /// <summary>
    /// PageRepository.GetPages(siteId) — <c>Where(item =&gt; item.SiteId == siteId &amp;&amp; item.UserId == null)</c>.
    /// Both arms discriminate: the SiteId arm keeps another site's pages out, and the <c>UserId == null</c>
    /// arm keeps PERSONALIZED pages out of the shared page list.
    /// </summary>
    [Fact]
    public async Task Pages_are_filtered_by_site_and_exclude_personalized_pages()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        await NewPages().AddAsync(NewPage(siteId, "/shared"));
        await NewPages().AddAsync(NewPage(otherSiteId, "/other-site"));
        var personalized = NewPage(siteId, "/personalized");
        personalized.UserId = 7;
        await NewPages().AddAsync(personalized);

        await using var session = FrameworkSessions.OpenQuerySession();
        var paths = session.Query<PageAggregate>()
            .Where(item => item.SiteId == siteId && item.UserId == null)
            .ToList()
            .ConvertAll(item => item.Path);

        Assert.Equal(["/shared"], paths);
    }

    /// <summary>
    /// PageRepository.GetPage(path, siteId) — <c>FirstOrDefault(item =&gt; item.Path == path &amp;&amp; item.SiteId == siteId)</c>.
    /// The path is unique only WITHIN a site, so the SiteId arm is what stops one site's URL resolving to
    /// another site's page.
    /// </summary>
    [Fact]
    public async Task A_page_is_found_by_path_within_its_own_site_only()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        await NewPages().AddAsync(NewPage(siteId, "/contact"));
        await NewPages().AddAsync(NewPage(otherSiteId, "/contact"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<PageAggregate>().FirstOrDefault(item => item.Path == "/contact" && item.SiteId == siteId);
        var foundOther = session.Query<PageAggregate>().FirstOrDefault(item => item.Path == "/contact" && item.SiteId == otherSiteId);

        Assert.NotNull(found);
        Assert.NotNull(foundOther);
        Assert.Equal(siteId, found!.SiteId);
        Assert.Equal(otherSiteId, foundOther!.SiteId);
        Assert.NotEqual(found.Id, foundOther.Id);
    }

    /// <summary>
    /// The cross-provider AGREEMENT guard for <c>Page.Path</c>, mirroring
    /// <see cref="A_url_mapping_lookup_is_case_insensitive_on_every_provider"/> and
    /// <see cref="A_normalized_column_filter_is_identical_on_every_provider"/>.
    /// <para>
    /// A row stored with a MIXED-CASE path (<c>Foo/Bar</c>) is found by a lowercased lookup (<c>foo/bar</c>)
    /// through the repository's exact read expression — <c>item.PathNormalized == "foo/bar"</c> — on EVERY
    /// provider. The earlier code filtered <c>item.Path</c> raw in <c>PageRepository.GetPageAsync(path, siteId)</c>:
    /// Driven on the real hosts, the raw filter MISSED on PostgreSQL (case-SENSITIVE) and HIT on RavenDB
    /// (case-INSENSITIVE). Normalizing the DATA and the ARGUMENT collapses that divergence, because for two
    /// lowercase strings ordinal and case-insensitive equality are the same relation. The stored case
    /// survives untouched.
    /// </para>
    /// <para>
    /// The write goes through the STORE with an aggregate built by hand — no <c>PageMapper</c> — so if the
    /// normalization lived in the mapper (the wrong place) <c>PathNormalized</c> would be NULL here and the
    /// assertion would find nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_page_lookup_is_case_insensitive_on_every_provider()
    {
        var siteId = await NewSiteId();
        await NewPages().AddAsync(NewPage(siteId, "Foo/Bar"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<PageAggregate>()
            .FirstOrDefault(item => item.SiteId == siteId && item.PathNormalized == "foo/bar");

        Assert.NotNull(found);
        Assert.Equal("Foo/Bar", found!.Path);          // the STORED case survives — normalized is a lookup key, not a replacement
        Assert.Equal("foo/bar", found.PathNormalized);
    }

    // --- Modules ---

    /// <summary>
    /// ModuleRepository.GetModules(siteId) — <c>Where(item =&gt; item.SiteId == siteId)</c>. The severed
    /// navigation: PageModule used to reach Module.SiteId through an Include; the filter must stay in the
    /// DATABASE rather than becoming an in-memory join, so this proves the provider filters it server-side.
    /// </summary>
    [Fact]
    public async Task Modules_are_filtered_by_site()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        await NewModules().AddAsync(NewModule(siteId));
        await NewModules().AddAsync(NewModule(otherSiteId));

        await using var session = FrameworkSessions.OpenQuerySession();
        var modules = session.Query<ModuleAggregate>().Where(item => item.SiteId == siteId).ToList();

        Assert.Equal(siteId, Assert.Single(modules).SiteId);
    }

    // --- Users ---

    /// <summary>
    /// UserRepository.GetUser(username) — <c>FirstOrDefault(item =&gt; item.Username == username)</c>. Runs on
    /// every authentication.
    /// </summary>
    [Fact]
    public async Task A_user_is_found_by_username()
    {
        await NewUsers().AddAsync(NewUser("host"));
        await NewUsers().AddAsync(NewUser("someone-else"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<UserAggregate>().FirstOrDefault(item => item.Username == "host");

        Assert.NotNull(found);
        Assert.Equal("host", found!.Username);
    }

    // --- Roles ---

    /// <summary>
    /// RoleRepository.GetRoles(siteId, includeGlobalRoles: true) —
    /// <c>Where(item =&gt; item.SiteId == siteId || item.SiteId == null)</c>, "the load-bearing read of the
    /// whole cluster".
    /// <para>
    /// <b>The null arm is the assertion.</b> A global role (SiteId == null) is Host — and Host is how the
    /// installer grants the host user its role, and how "All Users" permissions resolve. Drop the null arm and
    /// nothing throws: the query just returns fewer rows, the Host role is not found, and the host user is
    /// locked out of a brand-new install. The other site's role is here so the test cannot pass by returning
    /// everything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Global_roles_are_visible_to_every_site_and_other_sites_roles_are_not()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        await NewRoles().AddAsync(NewRole(null, "Host"));
        await NewRoles().AddAsync(NewRole(siteId, "Administrators"));
        await NewRoles().AddAsync(NewRole(otherSiteId, "Intruders"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var names = session.Query<RoleAggregate>()
            .Where(item => item.SiteId == siteId || item.SiteId == null)
            .ToList()
            .ConvertAll(item => item.Name);

        Assert.Contains("Host", names);            // the global role — the null arm
        Assert.Contains("Administrators", names);  // this site's role
        Assert.DoesNotContain("Intruders", names); // another site's role must not leak
    }

    // --- UserRoles ---

    /// <summary>
    /// UserRoleRepository.GetUserRoles(userId, siteId) — <c>Where(item =&gt; item.UserId == userId)</c> then
    /// <c>Where(item =&gt; roleIds.Contains(item.RoleId))</c>. The role-id set is resolved first and
    /// the fan-in must stay server-side. A stale/empty result here means an authenticated user silently holds
    /// no roles.
    /// </summary>
    [Fact]
    public async Task User_roles_are_filtered_by_the_resolved_role_id_set()
    {
        var userId = 1001;
        var granted = await NewRoleId("Granted", null);
        var withheld = await NewRoleId("Withheld", null);

        await NewUserRoles().AddAsync(NewUserRole(userId, granted));
        await NewUserRoles().AddAsync(NewUserRole(userId, withheld));
        await NewUserRoles().AddAsync(NewUserRole(2002, granted));

        var roleIds = new List<int> { granted };

        await using var session = FrameworkSessions.OpenQuerySession();
        var assignments = session.Query<UserRoleAggregate>()
            .Where(item => item.UserId == userId)
            .Where(item => roleIds.Contains(item.RoleId))
            .ToList();

        Assert.Equal(granted, Assert.Single(assignments).RoleId);
    }

    // --- Settings: one aggregate, TWO databases ---

    /// <summary>
    /// SettingRepository.GetSettings(entityName, entityId) —
    /// <c>Where(item =&gt; item.EntityName == entityName &amp;&amp; item.EntityId == entityId)</c>. The key is
    /// polymorphic and NOTHING enforces it: no foreign key, no unique index. Filter on EntityId alone and you
    /// silently serve another entity's settings.
    /// </summary>
    [Fact]
    public async Task Settings_are_isolated_by_entity_name_as_well_as_entity_id()
    {
        await NewSettings().AddAsync(NewSetting("Site", 1, "Title", "the site"));
        await NewSettings().AddAsync(NewSetting("Page", 1, "Title", "the page"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var siteSettings = session.Query<SettingAggregate>()
            .Where(item => item.EntityName == "Site" && item.EntityId == 1)
            .ToList();

        Assert.Equal("the site", Assert.Single(siteSettings).SettingValue);
    }

    /// <summary>
    /// <b>The point of the split, and the test that discriminates it.</b> Setting is the ONE framework-store
    /// aggregate mapped in BOTH databases: SettingRepository routes Host/Job/ModuleDefinition/Theme/master:*
    /// to the master store and everything else to the tenant store, then merges the two in memory.
    /// <para>
    /// So the two stores must be genuinely SEPARATE: a master setting must not be visible to a tenant read,
    /// and vice versa. Collapse them onto one database — which for RavenDB is the default that happens when
    /// nobody sets MasterDatabase, and which produces no error at all — and this test fails while every
    /// single-store test still passes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_master_and_tenant_setting_stores_are_separate_databases()
    {
        await NewMasterSettings().AddAsync(NewSetting("Host", 1, "SMTPHost", "smtp.master"));
        await NewSettings().AddAsync(NewSetting("Site", 1, "DefaultTheme", "TenantTheme"));

        await using var tenantSession = FrameworkSessions.OpenQuerySession();
        var tenantSideHost = tenantSession.Query<SettingAggregate>().Where(item => item.EntityName == "Host").ToList();
        var tenantSideSite = tenantSession.Query<SettingAggregate>().Where(item => item.EntityName == "Site").ToList();

        await using var masterSession = MasterSessions.OpenQuerySession();
        var masterSideHost = masterSession.Query<SettingAggregate>().Where(item => item.EntityName == "Host").ToList();
        var masterSideSite = masterSession.Query<SettingAggregate>().Where(item => item.EntityName == "Site").ToList();

        // The master setting is in the master database and ONLY there.
        Assert.Equal("smtp.master", Assert.Single(masterSideHost).SettingValue);
        Assert.Empty(tenantSideHost);

        // The tenant setting is in the tenant database and ONLY there.
        Assert.Equal("TenantTheme", Assert.Single(tenantSideSite).SettingValue);
        Assert.Empty(masterSideSite);
    }

    /// <summary>
    /// SettingRepository.GetEntityNames — <c>Select(item =&gt; item.EntityName).Distinct().OrderBy(item =&gt; item)</c>.
    /// Flagged as a translation hazard (src/Stomme.Server/README.md persistence seam): a projection + Distinct + OrderBy is the
    /// one shape in the framework store that is not a plain filter, and the one a document provider is least
    /// likely to translate. This runs it through the seam's own IQuerySession on both providers.
    /// </summary>
    [Fact]
    public async Task Entity_names_are_returned_distinct_and_ordered()
    {
        await NewSettings().AddAsync(NewSetting("Site", 1, "A", "1"));
        await NewSettings().AddAsync(NewSetting("Site", 2, "B", "2")); // same name, different entity => ONE name
        await NewSettings().AddAsync(NewSetting("Module", 1, "C", "3"));

        await using var session = FrameworkSessions.OpenQuerySession();
        // The projection + Distinct run in the database; the ordering is applied to the materialized set,
        // exactly as SettingRepository.GetEntityNames does. OrderBy(item => item) on the projected scalar is
        // the shape no document provider can express — see the test below and src/Stomme.Persistence.RavenDb/README.md.
        var names = session.Query<SettingAggregate>()
            .Select(item => item.EntityName)
            .Distinct()
            .ToList()
            .OrderBy(item => item)
            .ToList();

        Assert.Equal(["Module", "Site"], names);
    }

    /// <summary>
    /// SettingRepository.GetEntityIds(entityName) —
    /// <c>Where(EntityName).Select(item =&gt; item.EntityId).Distinct().OrderBy(item =&gt; item)</c>.
    /// </summary>
    [Fact]
    public async Task Entity_ids_are_returned_distinct_and_ordered_for_one_entity_name()
    {
        await NewSettings().AddAsync(NewSetting("Site", 2, "A", "1"));
        await NewSettings().AddAsync(NewSetting("Site", 1, "B", "2"));
        await NewSettings().AddAsync(NewSetting("Site", 2, "C", "3")); // duplicate id => ONE entry
        await NewSettings().AddAsync(NewSetting("Module", 9, "D", "4")); // another entity name => excluded

        await using var session = FrameworkSessions.OpenQuerySession();
        var ids = session.Query<SettingAggregate>()
            .Where(item => item.EntityName == "Site")
            .Select(item => item.EntityId)
            .Distinct()
            .ToList()
            .OrderBy(item => item)
            .ToList();

        Assert.Equal([1, 2], ids);
    }

    // --- Jobs: a MASTER-database cluster, read through MasterSessions ---

    /// <summary>
    /// JobRepository.GetJobAsync(jobType) — <c>FirstOrDefault(item =&gt; item.JobType == jobType)</c>. The
    /// scheduler's per-run lookup: <c>HostedServiceBase</c> resolves its own <see cref="JobAggregate"/> by its
    /// fully-qualified type name on every tick, and a miss triggers auto-registration.
    /// <para>
    /// Run through <see cref="MasterSessions"/>, not <see cref="FrameworkSessions"/> — Job lives in the master
    /// database, and reading it from the tenant side would find nothing on a real install. The decoy job with a
    /// different type is here so the test cannot pass by returning the only row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_job_is_found_by_its_fully_qualified_type()
    {
        await NewJobs().AddAsync(NewJob("Stomme.Infrastructure.NotificationJob, Stomme.Server"));
        await NewJobs().AddAsync(NewJob("Stomme.Infrastructure.PurgeJob, Stomme.Server"));

        await using var session = MasterSessions.OpenQuerySession();
        var found = session.Query<JobAggregate>()
            .FirstOrDefault(item => item.JobType == "Stomme.Infrastructure.NotificationJob, Stomme.Server");

        Assert.NotNull(found);
        Assert.Equal("Stomme.Infrastructure.NotificationJob, Stomme.Server", found!.JobType);
    }

    // --- JobLogs ---

    /// <summary>
    /// JobLogRepository.GetJobLogsAsync(jobId) — the log list for one job AND the <c>-1</c> "all" overload,
    /// COMPOSED IN C# rather than pushed into the predicate.
    /// <para>
    /// <b>This is the sentinel hazard, applied to this cluster.</b> The original query was
    /// <c>Where(item =&gt; item.JobId == jobId || jobId == -1)</c>. EF folds <c>-1 == -1</c> to true and drops
    /// the arm; RavenDB does NOT, and would compile the wildcard arm into RQL matching NOTHING — returning 0
    /// logs for the "all" call while passing on EF. The repository (and this test) compose the filter in C# so
    /// each provider is only ever sent an arm that narrows; the <c>-1</c> case sends no filter at all. Ordered
    /// by id descending, newest first, exactly as the Admin log screen shows them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Job_logs_are_listed_for_one_job_and_for_the_minus_one_wildcard()
    {
        var jobId = await NewJobId();
        var otherJobId = await NewJobId();

        await NewJobLogs().AddAsync(NewJobLog(jobId));
        await NewJobLogs().AddAsync(NewJobLog(jobId));
        await NewJobLogs().AddAsync(NewJobLog(otherJobId));

        await using var session = MasterSessions.OpenQuerySession();

        // (jobId) — the arm narrows: two logs for this job, newest first.
        Assert.Equal(2, Query(session, jobId).Count);
        // (-1) — everything. The overload GetJobLogsAsync() and JobLogController use; the one that returned zero
        // on RavenDB before the repository stopped pushing the sentinel into the expression tree.
        Assert.Equal(3, Query(session, -1).Count);

        // Copied from JobLogRepository.GetJobLogsAsync — composed in C#, so each provider is only ever sent a
        // filter that narrows.
        static List<JobLogAggregate> Query(IQuerySession session, int jobId)
        {
            var query = session.Query<JobLogAggregate>();

            if (jobId != -1)
            {
                query = query.Where(item => item.JobId == jobId);
            }

            return query.OrderByDescending(item => item.Id).ToList();
        }
    }

    /// <summary>
    /// JobLogRepository.PopulateJobsAsync — <c>Where(item =&gt; jobIds.Contains(item.Id))</c> over
    /// <see cref="JobAggregate"/>, the second query that replaces the removed <c>.Include(item =&gt; item.Job)</c>
    /// The Admin &gt; Jobs &gt; Log screen dereferences <c>log.Job.Name</c> and <c>log.Job.IsExecuting</c>,
    /// so a provider that cannot serve this key lookup hands every row a null Job and NREs the screen.
    /// <para>
    /// Note this is a KEY lookup — <c>item.Id</c>, which each provider maps differently (an int column on the
    /// relational side, a string document id on RavenDB). That asymmetry is why it belongs here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Jobs_are_found_by_the_id_list_their_logs_reference()
    {
        var jobId = await NewJobId();
        var otherJobId = await NewJobId();
        var unreferencedJobId = await NewJobId();

        var jobIds = new List<int> { jobId, otherJobId };

        await using var session = MasterSessions.OpenQuerySession();
        var jobs = session.Query<JobAggregate>()
            .Where(item => jobIds.Contains(item.Id))
            .ToList();

        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, item => item.Id == jobId);
        Assert.Contains(jobs, item => item.Id == otherJobId);
        Assert.DoesNotContain(jobs, item => item.Id == unreferencedJobId);
    }

    // --- Themes: a MASTER-database cluster, read through MasterSessions ---

    /// <summary>
    /// ThemeRepository.ProcessThemesAsync — <c>session.Query&lt;ThemeAggregate&gt;().ToList()</c>, the theme-sync
    /// read, plus the Version-column round-trip that is this cluster's OWN hazard.
    /// <para>
    /// Run through <see cref="MasterSessions"/>, not <see cref="FrameworkSessions"/> — Theme lives in the master
    /// database, and reading it from the tenant side would find nothing on a real install.
    /// </para>
    /// <para>
    /// <b>The Version assertion is the discriminating one.</b> The theme table's <c>version</c> column is a
    /// STRING, but <see cref="AggregateRoot{TId}.Version"/> is a <c>uint</c> concurrency token that shadows the
    /// name — the <c>SiteAggregate.SiteVersion</c> situation. <see cref="ThemeAggregate.ThemeVersion"/> carries
    /// the string and is mapped to <c>version</c> explicitly. Map it wrong — leave it to convention
    /// (<c>theme_version</c>) or fail to ignore the uint token — and this reads back null or throws on one
    /// provider. The decoy theme with a different name is here so the test cannot pass by returning the only row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_theme_round_trips_its_name_and_version_through_the_master_database()
    {
        await NewThemes().AddAsync(NewTheme("Stomme.Themes.StommeTheme, Stomme.Client", "Stomme Theme", "4.2.1"));
        await NewThemes().AddAsync(NewTheme("Stomme.Themes.BlazorTheme, Stomme.Client", "Blazor Theme", "1.0.0"));

        await using var session = MasterSessions.OpenQuerySession();
        var found = session.Query<ThemeAggregate>()
            .FirstOrDefault(item => item.ThemeName == "Stomme.Themes.StommeTheme, Stomme.Client");

        Assert.NotNull(found);
        Assert.Equal("Stomme Theme", found!.Name);
        Assert.Equal("4.2.1", found.ThemeVersion);   // the `version` column, past the concurrency-token shadow
    }

    // --- ModuleDefinitions: a MASTER-database cluster, read through MasterSessions ---

    /// <summary>
    /// ModuleDefinitionRepository.ProcessModuleDefinitionsAsync —
    /// <c>session.Query&lt;ModuleDefinitionAggregate&gt;().ToList()</c>, the module-discovery sync read, plus the
    /// Version-column round-trip that is this cluster's OWN hazard.
    /// <para>
    /// Run through <see cref="MasterSessions"/>, not <see cref="FrameworkSessions"/> — ModuleDefinition lives in
    /// the master database, and reading it from the tenant side would find nothing on a real install.
    /// </para>
    /// <para>
    /// <b>The Version assertion is the discriminating one.</b> The module_definition table's <c>version</c> column
    /// is a STRING, but <see cref="AggregateRoot{TId}.Version"/> is a <c>uint</c> concurrency token that shadows
    /// the name — the <c>ThemeAggregate.ThemeVersion</c> situation.
    /// <see cref="ModuleDefinitionAggregate.ModuleDefinitionVersion"/> carries the string and is mapped to
    /// <c>version</c> explicitly. Map it wrong and this reads back null or throws on one provider. The decoy with a
    /// different name is here so the test cannot pass by returning the only row. Name/Description/Categories are
    /// asserted too — this table maps four content columns where Theme mapped one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_module_definition_round_trips_its_columns_and_version_through_the_master_database()
    {
        await NewModuleDefinitions().AddAsync(NewModuleDefinition("Stomme.Modules.HtmlText, Stomme.Client", "HTML/Text", "Renders HTML", "Content", "4.2.1"));
        await NewModuleDefinitions().AddAsync(NewModuleDefinition("Stomme.Modules.Admin.Pages, Stomme.Client", "Pages", "Manage Pages", "Admin", "1.0.0"));

        await using var session = MasterSessions.OpenQuerySession();
        var found = session.Query<ModuleDefinitionAggregate>()
            .FirstOrDefault(item => item.ModuleDefinitionName == "Stomme.Modules.HtmlText, Stomme.Client");

        Assert.NotNull(found);
        Assert.Equal("HTML/Text", found!.Name);
        Assert.Equal("Renders HTML", found.Description);
        Assert.Equal("Content", found.Categories);
        Assert.Equal("4.2.1", found.ModuleDefinitionVersion);   // the `version` column, past the concurrency-token shadow
    }

    // --- Tenants: a MASTER-database cluster and half of the routing core, read through MasterSessions ---

    /// <summary>
    /// TenantRepository.GetTenantsAsync — <c>session.Query&lt;TenantAggregate&gt;().ToList()</c>, the routing read,
    /// plus the two hazards this cluster carries: the <c>db_type</c> provider-selector and the <c>version</c>-column
    /// shadow.
    /// <para>
    /// Run through <see cref="MasterSessions"/>, not <see cref="FrameworkSessions"/> — Tenant lives in the master
    /// database, and reading it from the tenant side would find nothing on a real install.
    /// </para>
    /// <para>
    /// <b>Two discriminating assertions.</b> <c>DBType</c> is the field that selects the persistence provider on every
    /// request; the builder does not declare its column, so a mapping that trusted the builder would read it back
    /// null and break routing for the whole install. And the <c>version</c> column is a STRING that
    /// <see cref="AggregateRoot{TId}.Version"/> (a uint token) shadows by name — <see cref="TenantAggregate.TenantVersion"/>
    /// carries it explicitly. The decoy tenant with a different name is here so the test cannot pass by returning the
    /// only row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_tenant_round_trips_its_db_type_and_version_through_the_master_database()
    {
        await NewTenants().AddAsync(NewTenant("Master", "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", "5.0.0"));
        await NewTenants().AddAsync(NewTenant("Contoso", "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server", "4.1.0"));

        await using var session = MasterSessions.OpenQuerySession();
        var found = session.Query<TenantAggregate>()
            .FirstOrDefault(item => item.Name == "Master");

        Assert.NotNull(found);
        Assert.Equal("Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", found!.DBType); // the provider-selector, absent from the builder
        Assert.Equal("5.0.0", found.TenantVersion);   // the `version` column, past the concurrency-token shadow
    }

    // --- Aliases: a MASTER-database cluster and the other half of the routing core, read through MasterSessions ---

    /// <summary>
    /// AliasRepository.GetAliasesAsync — <c>session.Query&lt;AliasAggregate&gt;().ToList()</c>, the read whose result
    /// the routing lookup matches IN MEMORY. Proves the alias row (including the builder-omitted <c>is_default</c>)
    /// round-trips through the master database on both providers, and that its <c>tenant_id</c>/<c>site_id</c>
    /// resolve correctly.
    /// <para>
    /// Run through <see cref="MasterSessions"/>, not <see cref="FrameworkSessions"/> — Alias lives in the master
    /// database. NOTE the discriminating value is <c>IsDefault</c>: <c>AliasEntityBuilder</c> does not declare the
    /// column (migration 03000201 adds it), so a mapping that trusted the builder would silently drop it and break
    /// default-alias redirection. The decoy alias is non-default so the test cannot pass by reading the only row.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_alias_round_trips_its_routing_columns_through_the_master_database()
    {
        await NewAliases().AddAsync(NewAlias("stomme.me", tenantId: 3, siteId: 7, isDefault: true));
        await NewAliases().AddAsync(NewAlias("stomme.me/products", tenantId: 3, siteId: 7, isDefault: false));

        await using var session = MasterSessions.OpenQuerySession();
        var found = session.Query<AliasAggregate>()
            .FirstOrDefault(item => item.Name == "stomme.me");

        Assert.NotNull(found);
        Assert.Equal(3, found!.TenantId);
        Assert.Equal(7, found.SiteId);
        Assert.True(found.IsDefault);   // the is_default column, absent from the builder but present in the live table
    }

    // --- Permissions ---

    /// <summary>
    /// PermissionRepository.GetPermissionsAsync(siteId, entityName) —
    /// <c>Where(item =&gt; item.SiteId == siteId &amp;&amp; item.EntityName == entityName)</c>. THE render path's
    /// hot read: every page and every module authorizes through it, so this is the single most-issued query
    /// in the framework store.
    /// <para>
    /// <b>Both arms discriminate, and they fail differently.</b> Drop the EntityName arm and a Page's
    /// permissions answer for a Module. Drop the SiteId arm and one site's grants authorize another site's
    /// content — a cross-tenant authorization leak that raises nothing. The two decoys below are exactly
    /// those two failures.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Permissions_are_filtered_by_site_and_entity_name()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        await NewPermissions().AddAsync(NewPermission(siteId, "Page", 1, "View", roleId: 10));
        await NewPermissions().AddAsync(NewPermission(siteId, "Module", 1, "View", roleId: 11));      // same site, other entity
        await NewPermissions().AddAsync(NewPermission(otherSiteId, "Page", 1, "View", roleId: 12));   // other site, same entity

        await using var session = FrameworkSessions.OpenQuerySession();
        var permissions = session.Query<PermissionAggregate>()
            .Where(item => item.SiteId == siteId && item.EntityName == "Page")
            .ToList();

        var permission = Assert.Single(permissions);
        Assert.Equal(siteId, permission.SiteId);
        Assert.Equal("Page", permission.EntityName);
        Assert.Equal(10, permission.RoleId);
    }

    /// <summary>
    /// PermissionRepository.UpdatePermissionsAsync — <c>Where(item =&gt; item.EntityId == entityId || item.EntityId == -1)</c>.
    /// <para>
    /// <b>The <c>-1</c> arm is the assertion.</b> <c>-1</c> is the sentinel for "every entity of this
    /// EntityName" and is how API-level permissions are expressed (see <see cref="PermissionAggregate"/>).
    /// A provider that serves the equality arm but not the disjunction returns the id-specific grants and
    /// silently drops the wildcard ones — the permission simply does not apply, and nothing reports it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Permissions_match_the_wildcard_entity_id_sentinel_as_well_as_an_exact_id()
    {
        var siteId = await NewSiteId();
        await NewPermissions().AddAsync(NewPermission(siteId, "Module", 5, "Edit", roleId: 20));   // this entity
        await NewPermissions().AddAsync(NewPermission(siteId, "Module", -1, "Edit", roleId: 21));  // EVERY entity
        await NewPermissions().AddAsync(NewPermission(siteId, "Module", 6, "Edit", roleId: 22));   // another entity

        await using var session = FrameworkSessions.OpenQuerySession();
        var entityIds = session.Query<PermissionAggregate>()
            .Where(item => item.SiteId == siteId && item.EntityName == "Module")
            .Where(item => item.EntityId == 5 || item.EntityId == -1)
            .ToList()
            .ConvertAll(item => item.EntityId);

        Assert.Equal(2, entityIds.Count);
        Assert.Contains(5, entityIds);
        Assert.Contains(-1, entityIds);
        Assert.DoesNotContain(6, entityIds);
    }

    /// <summary>
    /// PermissionRepository.GetPermissionAsync(permissionId) —
    /// <c>FirstOrDefault(item =&gt; item.Id == permissionId)</c>, plus the cluster's own invariant: a
    /// permission is granted to EITHER a RoleId or a UserId, never both, and <c>IsAuthorized</c> false is a
    /// DENY rather than an absence.
    /// <para>
    /// Round-tripping through the provider is the point. A document provider that drops a null
    /// <c>RoleId</c>, or that coerces <c>IsAuthorized = false</c> to a missing field read back as default,
    /// turns a deny into a grant — the one failure in this cluster that fails OPEN.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_user_deny_round_trips_with_its_null_role_and_its_false_authorization()
    {
        var siteId = await NewSiteId();
        var deny = NewPermission(siteId, "Folder", 3, "Browse", roleId: null);
        deny.UserId = 4242;
        deny.IsAuthorized = false;
        await NewPermissions().AddAsync(deny);

        var reloaded = await NewPermissions().GetByIdAsync(deny.Id)!;

        Assert.NotNull(reloaded);
        Assert.Null(reloaded!.RoleId);
        Assert.Equal(4242, reloaded.UserId);
        Assert.False(reloaded.IsAuthorized); // a DENY, not an absence
    }

    // --- Languages ---

    /// <summary>
    /// LanguageRepository.ClearDefaultAsync — <c>Where(item =&gt; item.SiteId == siteId).Where(item =&gt; item.IsDefault)</c>,
    /// the read behind "at most one default language per site".
    /// <para>
    /// <b>Why this one.</b> Nothing in the schema enforces that invariant; the repository enforces it by
    /// reading the site's current defaults and writing each one back false. The harvested code did it by
    /// mutating TRACKED entities, which the seam cannot do — so this read, and the write that follows
    /// it, ARE the invariant. A provider that cannot filter on a bool leaves every language default and
    /// nothing raises.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Default_languages_are_found_by_site_so_they_can_be_un_defaulted()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        await NewLanguages().AddAsync(NewLanguage(siteId, "en-US", isDefault: true));
        await NewLanguages().AddAsync(NewLanguage(siteId, "fr-FR", isDefault: false));  // same site, not default
        await NewLanguages().AddAsync(NewLanguage(otherSiteId, "de-DE", isDefault: true)); // other site's default

        await using var session = FrameworkSessions.OpenQuerySession();
        var defaults = session.Query<LanguageAggregate>()
            .Where(item => item.SiteId == siteId)
            .Where(item => item.IsDefault)
            .ToList();

        Assert.Equal("en-US", Assert.Single(defaults).Code);
    }

    // --- Logs ---

    /// <summary>
    /// LogRepository.GetLogsAsync — <c>Where(SiteId).Where(Level).OrderByDescending(LogDate).Take(rows)</c>.
    /// <para>
    /// <b>The Take is the assertion, and it must happen AFTER the ordering.</b> Log is the largest table in
    /// the tenant; the whole point of the cap is to not materialize it. A provider that applies the cap before
    /// the sort returns an arbitrary <c>rows</c> entries and then sorts those — the admin viewer silently
    /// stops showing the NEWEST events, which is the only thing anyone opens it for, and reports no error.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Logs_are_filtered_by_level_and_capped_to_the_newest_rows()
    {
        var siteId = await NewSiteId();
        var baseDate = new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        // Five errors, oldest to newest, plus an Information entry the Level filter must exclude.
        for (var day = 0; day < 5; day++)
        {
            await NewLogs().AddAsync(NewLog(siteId, "Error", baseDate.AddDays(day)));
        }

        await NewLogs().AddAsync(NewLog(siteId, "Information", baseDate.AddDays(10)));

        await using var session = FrameworkSessions.OpenQuerySession();
        var dates = session.Query<LogAggregate>()
            .Where(item => item.SiteId == siteId)
            .Where(item => item.Level == "Error")
            .OrderByDescending(item => item.LogDate)
            .Take(2)
            .ToList()
            .ConvertAll(item => item.LogDate);

        // The two NEWEST errors, newest first — not the Information entry, and not the oldest two.
        Assert.Equal([baseDate.AddDays(4), baseDate.AddDays(3)], dates);
    }

    // --- Profiles ---

    /// <summary>
    /// ProfileRepository.GetProfilesAsync(siteId) — <c>Where(item =&gt; item.SiteId == siteId || item.SiteId == null)</c>.
    /// <para>
    /// <b>The null arm is the assertion</b>, and it is the same shape as the Role cluster's global-role read.
    /// A null SiteId is a GLOBAL profile property shown on every site's profile form. Drop the arm and nothing
    /// throws: the global properties simply stop appearing everywhere at once. The other site's property is
    /// here so the test cannot pass by returning everything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Global_profile_properties_are_visible_to_every_site_and_other_sites_are_not()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        await NewProfiles().AddAsync(NewProfile(null, "Global"));
        await NewProfiles().AddAsync(NewProfile(siteId, "Mine"));
        await NewProfiles().AddAsync(NewProfile(otherSiteId, "Intruder"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var names = session.Query<ProfileAggregate>()
            .Where(item => item.SiteId == siteId || item.SiteId == null)
            .ToList()
            .ConvertAll(item => item.Name);

        Assert.Contains("Global", names);          // the global property — the null arm
        Assert.Contains("Mine", names);            // this site's property
        Assert.DoesNotContain("Intruder", names);  // another site's must not leak
    }

    // --- SiteTasks ---

    /// <summary>
    /// SiteTaskRepository.GetSiteTasksAsync — <c>Where(item =&gt; item.SiteId == siteId &amp;&amp; !item.IsCompleted).OrderBy(item =&gt; item.CreatedOn)</c>.
    /// <para>
    /// <b>The <c>!IsCompleted</c> arm is the assertion, and it fails in the worst available direction.</b>
    /// SiteTask.Type is a fully-qualified type name that SiteTaskJob reflects over and EXECUTES. A provider
    /// that cannot negate a bool hands the job every task the site has ever completed and it runs them all
    /// again. Note the same column drives DeleteSiteTasks in the opposite direction — see
    /// <see cref="SiteTaskAggregate.IsCompleted"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Site_tasks_return_the_pending_queue_oldest_first_and_never_completed_work()
    {
        var siteId = await NewSiteId();

        // Deliberately added newest-first, so an unordered provider cannot pass by insertion order.
        await NewSiteTasks().AddAsync(NewSiteTask(siteId, "second", isCompleted: false, createdOn: new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        await NewSiteTasks().AddAsync(NewSiteTask(siteId, "first", isCompleted: false, createdOn: new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        await NewSiteTasks().AddAsync(NewSiteTask(siteId, "done", isCompleted: true, createdOn: new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc)));

        await using var session = FrameworkSessions.OpenQuerySession();
        var names = session.Query<SiteTaskAggregate>()
            .Where(item => item.SiteId == siteId && !item.IsCompleted)
            .OrderBy(item => item.CreatedOn)
            .ToList()
            .ConvertAll(item => item.Name);

        Assert.Equal(["first", "second"], names);
    }

    // --- Notifications ---

    /// <summary>
    /// NotificationRepository.GetNotificationsAsync(siteId, -1, -1) — the DELIVERY QUEUE:
    /// <c>Where(SiteId).Where(IsDelivered == false &amp;&amp; IsDeleted == false).Where(SendOn == null || SendOn &lt; UtcNow)</c>.
    /// <para>
    /// <b>Every arm suppresses a message that must not be sent</b>, and each fails by SENDING it: an already
    /// delivered notification is sent twice, a soft-deleted one is sent after the user deleted it, and a
    /// future-dated one is sent early. The <c>SendOn == null</c> half of the last arm is the opposite failure —
    /// drop it and notifications with no scheduled time are never sent at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_delivery_queue_excludes_delivered_deleted_and_not_yet_due_notifications()
    {
        var siteId = await NewSiteId();
        var now = DateTime.UtcNow;

        var due = NewNotification(siteId, "due");
        due.SendOn = now.AddHours(-1);
        await NewNotifications().AddAsync(due);

        var unscheduled = NewNotification(siteId, "unscheduled");
        unscheduled.SendOn = null; // the null arm: must still be sent
        await NewNotifications().AddAsync(unscheduled);

        var delivered = NewNotification(siteId, "delivered");
        delivered.SendOn = now.AddHours(-1);
        delivered.IsDelivered = true;
        await NewNotifications().AddAsync(delivered);

        var deleted = NewNotification(siteId, "deleted");
        deleted.SendOn = now.AddHours(-1);
        deleted.IsDeleted = true;
        await NewNotifications().AddAsync(deleted);

        var future = NewNotification(siteId, "future");
        future.SendOn = now.AddDays(1);
        await NewNotifications().AddAsync(future);

        await using var session = FrameworkSessions.OpenQuerySession();
        var subjects = session.Query<NotificationAggregate>()
            .Where(item => item.SiteId == siteId)
            .Where(item => item.IsDelivered == false && item.IsDeleted == false)
            .Where(item => item.SendOn == null || item.SendOn < DateTime.UtcNow)
            .ToList()
            .ConvertAll(item => item.Subject);

        Assert.Contains("due", subjects);
        Assert.Contains("unscheduled", subjects);
        Assert.DoesNotContain("delivered", subjects);
        Assert.DoesNotContain("deleted", subjects);
        Assert.DoesNotContain("future", subjects);
    }

    /// <summary>
    /// NotificationRepository.ReadPurgeBatchAsync — the purge predicate, whose PRECEDENCE is the whole point:
    /// <c>FromUserId == null &amp;&amp; (IsDeleted || IsDelivered &amp;&amp; DeliveredOn &lt; purgedate)</c>.
    /// <para>
    /// <b>Two ways to get this wrong, both silent and both destructive.</b> Lose the <c>FromUserId == null</c>
    /// arm and the nightly purge starts deleting USER-TO-USER notifications. Lose the parentheses and
    /// <c>&amp;&amp;</c> binds tighter, so a soft-deleted notification must ALSO be old to be purged — the
    /// table then grows forever. This runs the real predicate through the seam's own IQuerySession on both
    /// providers.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_purge_takes_only_system_notifications_that_are_deleted_or_delivered_and_old()
    {
        var siteId = await NewSiteId();
        var purgedate = DateTime.UtcNow.AddDays(-30);

        var deleted = NewNotification(siteId, "system-deleted");
        deleted.IsDeleted = true; // deleted, and NOT old: the parentheses arm
        await NewNotifications().AddAsync(deleted);

        var oldDelivered = NewNotification(siteId, "system-old-delivered");
        oldDelivered.IsDelivered = true;
        oldDelivered.DeliveredOn = purgedate.AddDays(-1);
        await NewNotifications().AddAsync(oldDelivered);

        var recentDelivered = NewNotification(siteId, "system-recent-delivered");
        recentDelivered.IsDelivered = true;
        recentDelivered.DeliveredOn = DateTime.UtcNow; // delivered but not old: keep
        await NewNotifications().AddAsync(recentDelivered);

        var fromUser = NewNotification(siteId, "user-to-user");
        fromUser.FromUserId = 42; // a USER sent it: never purged, whatever its state
        fromUser.IsDeleted = true;
        await NewNotifications().AddAsync(fromUser);

        await using var session = FrameworkSessions.OpenQuerySession();
        var subjects = session.Query<NotificationAggregate>()
            .Where(item => item.SiteId == siteId && item.FromUserId == null
                && (item.IsDeleted || item.IsDelivered && item.DeliveredOn < purgedate))
            .ToList()
            .ConvertAll(item => item.Subject);

        Assert.Contains("system-deleted", subjects);
        Assert.Contains("system-old-delivered", subjects);
        Assert.DoesNotContain("system-recent-delivered", subjects);
        Assert.DoesNotContain("user-to-user", subjects);
    }

    // --- SiteGroups ---

    /// <summary>
    /// SiteGroupMemberRepository.GetSiteGroupMembersAsync(siteId, siteGroupId) — the -1 wildcards, composed.
    /// <para>
    /// <b>THIS TEST ALREADY EARNED ITS KEEP.</b> The harvested query pushed the sentinel INTO the predicate:
    /// <c>Where((siteId == -1 || item.SiteId == siteId) &amp;&amp; (siteGroupId == -1 || item.SiteGroupId == siteGroupId))</c>.
    /// EF folds <c>-1 == -1</c> to true and drops the arm; RavenDB's LINQ provider does NOT, and compiled the
    /// wildcard arm into RQL that matched nothing — this test returned <b>0 of 2</b> members on RavenDB while
    /// passing on EF. Since SynchronizationJob and ServerSiteService both read through the (-1, -1) overload,
    /// a provider swap would have silently stopped every site synchronization with no exception and no log
    /// line. The repository now composes the filter in C# and only sends the provider the arms that narrow;
    /// this test is the copy of that shape and the reason it is known to work on both.
    /// </para>
    /// <para>
    /// All four call shapes are exercised because production uses all four: (-1, -1) from SynchronizationJob
    /// and ServerSiteService, (-1, groupId) from Admin Site/Index.razor, (siteId, groupId) from
    /// SiteGroupMemberService, and (siteId, -1) from the controller's route.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Site_group_members_are_found_by_site_and_group_and_by_the_minus_one_wildcards()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        var groupId = await NewSiteGroupId(SiteGroupTypes.Synchronization, primarySiteId: siteId);
        var otherGroupId = await NewSiteGroupId(SiteGroupTypes.Localization, primarySiteId: otherSiteId);

        await NewSiteGroupMembers().AddAsync(NewSiteGroupMember(groupId, siteId));
        await NewSiteGroupMembers().AddAsync(NewSiteGroupMember(groupId, otherSiteId));
        await NewSiteGroupMembers().AddAsync(NewSiteGroupMember(otherGroupId, siteId));

        await using var session = FrameworkSessions.OpenQuerySession();

        // (siteId, groupId) — both arms narrow.
        Assert.Single(Query(session, siteId, groupId));
        // (-1, groupId) — the site arm is the wildcard. Admin Site/Index.razor's read.
        Assert.Equal(2, Query(session, -1, groupId).Count);
        // (siteId, -1) — the group arm is the wildcard.
        Assert.Equal(2, Query(session, siteId, -1).Count);
        // (-1, -1) — everything. SynchronizationJob's and ServerSiteService's read, and the one that returned
        // zero on RavenDB before the repository stopped pushing the sentinel into the expression tree.
        Assert.Equal(3, Query(session, -1, -1).Count);

        // Copied from SiteGroupMemberRepository.GetSiteGroupMembersAsync — composed in C#, so each provider is
        // only ever sent a filter that narrows.
        static List<SiteGroupMemberAggregate> Query(IQuerySession session, int siteId, int siteGroupId)
        {
            var query = session.Query<SiteGroupMemberAggregate>();

            if (siteId != -1)
            {
                query = query.Where(item => item.SiteId == siteId);
            }

            if (siteGroupId != -1)
            {
                query = query.Where(item => item.SiteGroupId == siteGroupId);
            }

            return query.ToList();
        }
    }

    /// <summary>
    /// SiteGroupMemberRepository.PopulateSiteGroups — <c>Where(item =&gt; siteGroupIds.Contains(item.Id))</c>.
    /// <para>
    /// <b>The most load-bearing read this increment added, and the newest query shape on the seam.</b> This IS
    /// the severed <c>.Include(item =&gt; item.SiteGroup)</c>: the pair could not stay joined by a navigation
    /// (EF rediscovered the SiteGroup contract through it and broke the model), so the eager load became a
    /// second query that fetches the referenced groups by id and stitches them on in memory. Twenty-two
    /// production reads dereference the result — SynchronizationJob branches its whole behaviour on
    /// <c>member.SiteGroup.Type</c>. A provider that cannot serve <c>Contains</c> over a key list hands every
    /// one of them a null group and throws NullReferenceException on a background job.
    /// </para>
    /// <para>
    /// Note this is a KEY lookup, not a scalar filter — <c>item.Id</c>, which each provider maps differently
    /// (an int column on the relational side, a string document id on RavenDB). That asymmetry is exactly why
    /// this belongs in a conformance test rather than only in the EF cluster tests.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Site_groups_are_found_by_the_id_list_their_members_reference()
    {
        var siteId = await NewSiteId();
        var groupId = await NewSiteGroupId(SiteGroupTypes.Synchronization, primarySiteId: siteId);
        var otherGroupId = await NewSiteGroupId(SiteGroupTypes.Localization, primarySiteId: siteId);
        var unreferencedGroupId = await NewSiteGroupId(SiteGroupTypes.ChangeDetection, primarySiteId: siteId);

        var siteGroupIds = new List<int> { groupId, otherGroupId };

        await using var session = FrameworkSessions.OpenQuerySession();
        var siteGroups = session.Query<SiteGroupAggregate>()
            .Where(item => siteGroupIds.Contains(item.Id))
            .ToList();

        Assert.Equal(2, siteGroups.Count);
        Assert.Contains(siteGroups, item => item.Id == groupId);
        Assert.Contains(siteGroups, item => item.Id == otherGroupId);
        // ...and the group nobody references is NOT fetched. The stitch reads the groups its members name, not
        // every group in the tenant.
        Assert.DoesNotContain(siteGroups, item => item.Id == unreferencedGroupId);

        // The stitch itself: every member gets ITS group, keyed by id.
        var byId = siteGroups.ToDictionary(item => item.Id);
        Assert.Equal(SiteGroupTypes.Synchronization, byId[groupId].Type);
        Assert.Equal(SiteGroupTypes.Localization, byId[otherGroupId].Type);
    }

    /// <summary>
    /// SiteGroupRepository.GetSiteGroupsAsync — the unfiltered read, and SynchronizationJob's entry point.
    /// <para>
    /// The job filters <c>Where(item.Synchronize &amp;&amp; (Type == Synchronization || Type == ChangeDetection))</c>
    /// in MEMORY over the result, exactly as it did before this cluster moved — so what the provider must get
    /// right is that every group comes back with its Type and Synchronize intact. A group whose Synchronize
    /// round-tripped wrong is either never synchronized or synchronized forever.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_site_group_round_trips_the_flags_the_synchronization_job_filters_on()
    {
        var siteId = await NewSiteId();
        var queuedId = await NewSiteGroupId(SiteGroupTypes.Synchronization, primarySiteId: siteId, synchronize: true);
        var idleId = await NewSiteGroupId(SiteGroupTypes.ChangeDetection, primarySiteId: siteId, synchronize: false);

        await using var session = FrameworkSessions.OpenQuerySession();
        var groups = session.Query<SiteGroupAggregate>().ToList().ToDictionary(item => item.Id);

        Assert.True(groups[queuedId].Synchronize);
        Assert.Equal(SiteGroupTypes.Synchronization, groups[queuedId].Type);
        Assert.False(groups[idleId].Synchronize);
        Assert.Equal(SiteGroupTypes.ChangeDetection, groups[idleId].Type);
        Assert.Equal(siteId, groups[queuedId].PrimarySiteId);
    }

    /// <summary>
    /// The pair's write path: a member's SynchronizedOn survives a read-then-write, and null means "never".
    /// <para>
    /// SynchronizationJob reads a null SynchronizedOn as "never synchronized" and writes DateTime.MinValue to
    /// mean "initializing", then the real timestamp when it finishes. A provider that coalesced the null (or
    /// could not store one) would make every site look freshly initialized on every run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_members_synchronized_on_stores_a_null_and_then_a_timestamp()
    {
        var siteId = await NewSiteId();
        var groupId = await NewSiteGroupId(SiteGroupTypes.Synchronization, primarySiteId: siteId);

        var member = NewSiteGroupMember(groupId, siteId);
        await NewSiteGroupMembers().AddAsync(member);

        await using (var session = FrameworkSessions.OpenQuerySession())
        {
            Assert.Null(session.Query<SiteGroupMemberAggregate>().First(item => item.Id == member.Id).SynchronizedOn);
        }

        // The read-through-a-session, write-through-the-store shape the repository uses.
        var synchronizedOn = new DateTime(2024, 8, 9, 10, 11, 12, DateTimeKind.Utc);
        SiteGroupMemberAggregate detached;
        await using (var session = FrameworkSessions.OpenQuerySession())
        {
            detached = session.Query<SiteGroupMemberAggregate>().First(item => item.Id == member.Id);
        }

        detached.SynchronizedOn = synchronizedOn;
        await NewSiteGroupMembers().UpdateAsync(detached);

        await using (var session = FrameworkSessions.OpenQuerySession())
        {
            Assert.Equal(synchronizedOn, session.Query<SiteGroupMemberAggregate>().First(item => item.Id == member.Id).SynchronizedOn);
        }
    }

    // --- Visitors ---

    /// <summary>
    /// VisitorRepository.GetVisitorsAsync(siteId, fromDate) —
    /// <c>Where(item =&gt; item.SiteId == siteId &amp;&amp; item.VisitedOn &gt;= fromDate)</c>, the Admin &gt;
    /// Visitors report.
    /// <para>
    /// <b>The date arm is an INCLUSIVE lower bound and the boundary is the assertion.</b> The admin picks a
    /// date and expects that day's visits included; a provider that translates <c>&gt;=</c> as <c>&gt;</c>
    /// silently drops every visit landing exactly on the cutoff. The other site's visitor is here so the test
    /// cannot pass by returning everything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Visitors_are_filtered_by_site_and_from_an_inclusive_date()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        var fromDate = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        await NewVisitors().AddAsync(NewVisitor(siteId, "1.1.1.1", fromDate));                  // exactly ON the bound
        await NewVisitors().AddAsync(NewVisitor(siteId, "2.2.2.2", fromDate.AddDays(1)));       // after
        await NewVisitors().AddAsync(NewVisitor(siteId, "3.3.3.3", fromDate.AddDays(-1)));      // before: excluded
        await NewVisitors().AddAsync(NewVisitor(otherSiteId, "4.4.4.4", fromDate.AddDays(1)));  // another site: excluded

        await using var session = FrameworkSessions.OpenQuerySession();
        var addresses = session.Query<VisitorAggregate>()
            .Where(item => item.SiteId == siteId && item.VisitedOn >= fromDate)
            .ToList()
            .ConvertAll(item => item.IPAddress);

        Assert.Contains("1.1.1.1", addresses); // the inclusive bound
        Assert.Contains("2.2.2.2", addresses);
        Assert.DoesNotContain("3.3.3.3", addresses);
        Assert.DoesNotContain("4.4.4.4", addresses);
    }

    /// <summary>
    /// VisitorRepository.GetVisitorAsync(siteId, IPAddress) —
    /// <c>FirstOrDefault(item =&gt; item.SiteId == siteId &amp;&amp; item.IPAddress == IPAddress)</c>. THE
    /// render-path read: App.razor correlates a cookie-less visitor by IP on every request to a site with
    /// VisitorTracking on.
    /// <para>
    /// The SiteId arm is what stops one site's visitor being handed to another site — the same IP visits many
    /// sites in one installation, so without it a visitor's identity leaks across tenants on the render path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_visitor_is_correlated_by_ip_within_its_own_site_only()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        await NewVisitors().AddAsync(NewVisitor(siteId, "9.9.9.9", DateTime.UtcNow));
        await NewVisitors().AddAsync(NewVisitor(otherSiteId, "9.9.9.9", DateTime.UtcNow));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<VisitorAggregate>().FirstOrDefault(item => item.SiteId == siteId && item.IPAddress == "9.9.9.9");
        var foundOther = session.Query<VisitorAggregate>().FirstOrDefault(item => item.SiteId == otherSiteId && item.IPAddress == "9.9.9.9");

        Assert.NotNull(found);
        Assert.NotNull(foundOther);
        Assert.Equal(siteId, found!.SiteId);
        Assert.Equal(otherSiteId, foundOther!.SiteId);
        Assert.NotEqual(found.Id, foundOther.Id); // the same IP is TWO visitors, one per site
    }

    // --- UrlMappings ---

    /// <summary>
    /// UrlMappingRepository.GetUrlMappingsAsync(siteId, isMapped) — the table split on
    /// <c>string.IsNullOrEmpty(item.MappedUrl)</c>, in BOTH directions.
    /// <para>
    /// <b>This is the sentinel-shaped hazard, and the reason it is here rather than only in the
    /// EF tests.</b> <c>string.IsNullOrEmpty(x)</c> is not a comparison — it is a METHOD CALL that each
    /// provider must recognize and rewrite (EF emits <c>x IS NULL OR x = ''</c>). The <c>-1 == -1</c> fold proved
    /// that a shape EF folds silently is not a shape RavenDB folds, and it returned 0 of 2 rows while passing
    /// on EF. The failure here would be just as quiet and just as total: the admin's redirect list and the
    /// broken-link report are the two halves of this one predicate, so a provider that mistranslates it serves
    /// each screen the other's rows — or nothing.
    /// </para>
    /// <para>
    /// Both directions are asserted because the repository issues both, and they fail independently: the
    /// negated arm (<c>!IsNullOrEmpty</c>) is a separate translation from the plain one. The purge in
    /// DeleteUrlMappingsAsync reads through the unmapped arm too, and it DELETES — a provider that inverted it
    /// would purge the admin's configured redirects.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Url_mappings_split_into_the_mapped_and_the_unmapped_halves()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        await NewUrlMappings().AddAsync(NewUrlMapping(siteId, "old/path", "new/path")); // MAPPED: a redirect
        await NewUrlMappings().AddAsync(NewUrlMapping(siteId, "broken/link", ""));      // UNMAPPED: a broken link
        await NewUrlMappings().AddAsync(NewUrlMapping(otherSiteId, "other/site", "")); // another site: excluded from both

        await using var session = FrameworkSessions.OpenQuerySession();

        var mapped = session.Query<UrlMappingAggregate>()
            .Where(item => item.SiteId == siteId)
            .Where(item => !string.IsNullOrEmpty(item.MappedUrl))
            .ToList()
            .ConvertAll(item => item.Url);

        var unmapped = session.Query<UrlMappingAggregate>()
            .Where(item => item.SiteId == siteId)
            .Where(item => string.IsNullOrEmpty(item.MappedUrl))
            .ToList()
            .ConvertAll(item => item.Url);

        Assert.Equal(["old/path"], mapped);
        Assert.Equal(["broken/link"], unmapped);
    }

    /// <summary>
    /// UrlMappingRepository.GetUrlMappingAsync(siteId, url, referrer) —
    /// <c>FirstOrDefault(item =&gt; item.SiteId == siteId &amp;&amp; item.UrlNormalized == urlNormalized)</c>.
    /// App.razor's 404 handler runs this on every request that does not resolve to a page.
    /// <para>
    /// An EXACT match on the NORMALIZED lookup key — not StartsWith. The url is stored without its
    /// leading slash and truncated to 750 characters by the repository before it gets here; a provider that
    /// matched loosely would redirect one URL to another's target. Before the normalized column, the read filtered <c>Url</c>
    /// raw, which case-diverged behind the seam (see
    /// <see cref="A_url_mapping_lookup_is_case_insensitive_on_every_provider"/>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_url_mapping_is_found_by_exact_url_within_its_own_site()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        await NewUrlMappings().AddAsync(NewUrlMapping(siteId, "products", "catalogue"));
        await NewUrlMappings().AddAsync(NewUrlMapping(siteId, "products/widgets", "catalogue/widgets"));
        await NewUrlMappings().AddAsync(NewUrlMapping(otherSiteId, "products", "somewhere-else"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<UrlMappingAggregate>().FirstOrDefault(item => item.SiteId == siteId && item.UrlNormalized == "products");

        Assert.NotNull(found);
        // The exact row, not the prefix-matching sibling and not the other site's.
        Assert.Equal("catalogue", found!.MappedUrl);
    }

    /// <summary>
    /// The cross-provider AGREEMENT guard for <c>UrlMapping.Url</c>, mirroring
    /// <see cref="A_normalized_column_filter_is_identical_on_every_provider"/> for Folder/File.
    /// <para>
    /// A row stored with a MIXED-CASE url (<c>Foo/Bar</c>) is found by a lowercased lookup (<c>foo/bar</c>)
    /// through the repository's exact read expression — <c>item.UrlNormalized == "foo/bar"</c> — on EVERY
    /// provider. The earlier read filtered <c>item.Url</c> raw: driven on the real hosts, it MISSED on
    /// PostgreSQL (case-SENSITIVE) and HIT on RavenDB (case-INSENSITIVE). Normalizing the DATA and the
    /// ARGUMENT collapses that divergence, because for two lowercase strings ordinal and case-insensitive
    /// equality are the same relation. The stored case survives untouched.
    /// </para>
    /// <para>
    /// The write goes through the STORE with an aggregate built by hand — no <c>UrlMappingMapper</c> — so if
    /// the normalization lived in the mapper (the wrong place) <c>UrlNormalized</c> would be NULL here and the
    /// assertion would find nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_url_mapping_lookup_is_case_insensitive_on_every_provider()
    {
        var siteId = await NewSiteId();
        await NewUrlMappings().AddAsync(NewUrlMapping(siteId, "Foo/Bar", ""));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<UrlMappingAggregate>()
            .FirstOrDefault(item => item.SiteId == siteId && item.UrlNormalized == "foo/bar");

        Assert.NotNull(found);
        Assert.Equal("Foo/Bar", found!.Url);          // the STORED case survives — normalized is a lookup key, not a replacement
        Assert.Equal("foo/bar", found.UrlNormalized);
    }

    // --- PageModules ---

    /// <summary>
    /// PageModuleRepository.GetPageModulesAsync(siteId) — <c>Where(item =&gt; moduleIds.Contains(item.ModuleId))</c>.
    /// <para>
    /// <b>THE RENDER PATH'S OWN READ.</b> Every page render enumerates its page's module instances through
    /// this. PageModule.Module is no longer navigable, so the site filter can no longer ride a join: the repository
    /// resolves the site's module ids first and fans in on <c>ModuleId IN (...)</c>, which MUST stay in the
    /// database — on a shared tenant database (many sites, one database) an in-memory filter would read every
    /// other site's page modules on every request. A provider that cannot serve Contains over a key list
    /// renders every page blank.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Page_modules_are_fanned_in_by_the_resolved_module_id_set()
    {
        var mine = 900001;
        var alsoMine = 900002;
        var anotherSites = 900003;

        await NewPageModules().AddAsync(NewPageModule(pageId: 1, moduleId: mine, pane: "Content", order: 1));
        await NewPageModules().AddAsync(NewPageModule(pageId: 2, moduleId: alsoMine, pane: "Content", order: 1));
        await NewPageModules().AddAsync(NewPageModule(pageId: 3, moduleId: anotherSites, pane: "Content", order: 1));

        var moduleIds = new List<int> { mine, alsoMine };

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<PageModuleAggregate>()
            .Where(item => moduleIds.Contains(item.ModuleId))
            .ToList()
            .ConvertAll(item => item.ModuleId);

        Assert.Equal(2, found.Count);
        Assert.Contains(mine, found);
        Assert.Contains(alsoMine, found);
        Assert.DoesNotContain(anotherSites, found); // another site's instances must not leak onto the page
    }

    /// <summary>
    /// PageModuleRepository.CountModuleInstancesAsync — <c>Query&lt;PageModuleAggregate&gt;().Count(item =&gt; item.ModuleId == moduleId)</c>,
    /// the input to <c>Module.IsShared</c>.
    /// <para>
    /// <b>The COUNT must be computed BY THE PROVIDER, not by materializing the rows.</b> The harvested call was
    /// <c>db.PageModule.CountAsync(...)</c> — a database COUNT. The seam's IRepository has no count primitive,
    /// so the tempting port is <c>.Where(...).ToList().Count</c>, which fetches every instance of the module to
    /// measure the list. <c>Count(predicate)</c> over the session's IQueryable composes into each provider's
    /// own query instead — but "both providers translate Count" is exactly the kind of assumption the sentinel fold punished
    /// so it is asserted here rather than assumed. A provider that mistranslates the
    /// predicate reports the wrong instance count, and IsShared flips: a shared module is offered for deletion
    /// as if it were on one page.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_module_instance_count_is_computed_by_the_provider_not_by_materializing_the_rows()
    {
        var shared = 910001;
        var solitary = 910002;

        // The shared module is instanced on two pages...
        await NewPageModules().AddAsync(NewPageModule(pageId: 1, moduleId: shared, pane: "Content", order: 1));
        await NewPageModules().AddAsync(NewPageModule(pageId: 2, moduleId: shared, pane: "Content", order: 1));
        // ...the solitary one on exactly one.
        await NewPageModules().AddAsync(NewPageModule(pageId: 1, moduleId: solitary, pane: "Sidebar", order: 1));

        await using var session = FrameworkSessions.OpenQuerySession();

        Assert.Equal(2, session.Query<PageModuleAggregate>().Count(item => item.ModuleId == shared));
        Assert.Equal(1, session.Query<PageModuleAggregate>().Count(item => item.ModuleId == solitary));

        // ...which is what IsShared actually asks.
        Assert.True(session.Query<PageModuleAggregate>().Count(item => item.ModuleId == shared) > 1);
        Assert.False(session.Query<PageModuleAggregate>().Count(item => item.ModuleId == solitary) > 1);
    }

    /// <summary>
    /// PageModuleRepository.GetPageModuleAsync(pageId, moduleId) —
    /// <c>SingleOrDefault(item =&gt; item.PageId == pageId &amp;&amp; item.ModuleId == moduleId)</c>, plus the
    /// round trip of the columns the render path actually draws.
    /// <para>
    /// <c>Pane</c> and <c>Order</c> decide WHERE on the page a module appears and in what sequence. Note
    /// <c>Order</c> is a RESERVED WORD in every relational provider and a plain field on RavenDB — an
    /// asymmetry that is exactly why this belongs in a conformance test. A round trip that lost Order would
    /// shuffle every pane silently.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_page_module_is_found_by_page_and_module_and_round_trips_its_reserved_word_order()
    {
        var pageId = 920001;
        var moduleId = 920002;

        var pageModule = NewPageModule(pageId, moduleId, pane: "Sidebar", order: 4);
        await NewPageModules().AddAsync(pageModule);
        await NewPageModules().AddAsync(NewPageModule(pageId, moduleId + 1, pane: "Content", order: 1));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<PageModuleAggregate>()
            .SingleOrDefault(item => item.PageId == pageId && item.ModuleId == moduleId);

        Assert.NotNull(found);
        Assert.Equal("Sidebar", found!.Pane);
        Assert.Equal(4, found.Order); // the reserved word survived the round trip
    }

    // --- Folders ---

    /// <summary>
    /// FolderRepository.GetFoldersAsync(siteId) — <c>Where(item =&gt; item.SiteId == siteId)</c>, the read
    /// behind Admin &gt; Files and behind every folder picker in the product.
    /// <para>
    /// On a shared tenant database (many sites, one database) the SiteId arm is what stops one site's folder
    /// tree — and therefore its file listings — appearing on another. The hierarchy walk that follows is in
    /// memory and always was; what must stay in the database is this.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Folders_are_filtered_by_site()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        await NewFolders().AddAsync(NewFolder(siteId, "Public/"));
        await NewFolders().AddAsync(NewFolder(siteId, "Users/"));
        await NewFolders().AddAsync(NewFolder(otherSiteId, "Public/"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var paths = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId)
            .ToList()
            .ConvertAll(item => item.Path);

        Assert.Equal(2, paths.Count);
        Assert.Contains("Public/", paths);
        Assert.Contains("Users/", paths);
    }

    /// <summary>
    /// FolderRepository.GetFolderAsync(siteId, path) —
    /// <c>FirstOrDefault(item =&gt; item.SiteId == siteId &amp;&amp; item.Path == path)</c>. UserRepository,
    /// UserManager, FolderController and DefaultSiteTemplate all resolve a well-known folder through it.
    /// <para>
    /// The SiteId arm is the assertion: <c>Users/</c> exists in every site, so without it one site's user
    /// folder is handed to another — and <c>UserManager</c> creates the new user's directory under whatever
    /// comes back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_folder_is_found_by_path_within_its_own_site_only()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();
        await NewFolders().AddAsync(NewFolder(siteId, "Users/"));
        await NewFolders().AddAsync(NewFolder(otherSiteId, "Users/"));

        await using var session = FrameworkSessions.OpenQuerySession();
        var found = session.Query<FolderAggregate>().FirstOrDefault(item => item.SiteId == siteId && item.Path == "Users/");
        var foundOther = session.Query<FolderAggregate>().FirstOrDefault(item => item.SiteId == otherSiteId && item.Path == "Users/");

        Assert.NotNull(found);
        Assert.NotNull(foundOther);
        Assert.Equal(siteId, found!.SiteId);
        Assert.Equal(otherSiteId, foundOther!.SiteId);
        Assert.NotEqual(found.Id, foundOther.Id);
    }

    /// <summary>
    /// The folder's render-path payload, round-tripped: <c>Type</c> and <c>Path</c> resolve the physical
    /// directory, <c>ImageSizes</c> and <c>CacheControl</c> are read by <c>Pages/Files.cshtml.cs</c> on every
    /// file served, and <c>Order</c> is a RESERVED WORD on every relational provider and a plain field on
    /// RavenDB — the asymmetry that makes this a conformance concern rather than an EF one.
    /// </summary>
    [Fact]
    public async Task A_folder_round_trips_the_columns_the_render_path_reads_and_its_reserved_word_order()
    {
        var siteId = await NewSiteId();
        var folder = NewFolder(siteId, "Public/Images/");
        folder.Order = 4;
        folder.Type = "Public";
        folder.ImageSizes = "200x200,x100";
        folder.CacheControl = "public, max-age=3600";
        await NewFolders().AddAsync(folder);

        var reloaded = await NewFolders().GetByIdAsync(folder.Id)!;

        Assert.NotNull(reloaded);
        Assert.Equal(4, reloaded!.Order); // the reserved word survived
        Assert.Equal("Public", reloaded.Type);
        Assert.Equal("200x200,x100", reloaded.ImageSizes);
        Assert.Equal("public, max-age=3600", reloaded.CacheControl);
        Assert.Equal("Public/Images/", reloaded.Path);
    }

    /// <summary>
    /// A root folder's null ParentId round-trips. The hierarchy walk in
    /// <c>FolderRepository.GetFoldersHierarchy</c> keys off <c>ParentId == null</c> to find the roots, so a
    /// provider that coalesced the null would produce a tree with no root and an empty folder list.
    /// </summary>
    [Fact]
    public async Task A_root_folder_round_trips_its_null_parent()
    {
        var siteId = await NewSiteId();
        var root = NewFolder(siteId, "");
        root.ParentId = null;
        await NewFolders().AddAsync(root);

        var child = NewFolder(siteId, "Public/");
        child.ParentId = root.Id;
        await NewFolders().AddAsync(child);

        await using var session = FrameworkSessions.OpenQuerySession();
        var folders = session.Query<FolderAggregate>().Where(item => item.SiteId == siteId).ToList();

        Assert.Null(folders.Single(item => item.Id == root.Id).ParentId);
        Assert.Equal(root.Id, folders.Single(item => item.Id == child.Id).ParentId);
    }

    /// <summary>
    /// <b>THE MEASUREMENT THAT KEPT THE FILE CLUSTER OFF THE SEAM, run against both providers. It still
    /// passes, and the normalizing change did NOT make it obsolete — read the note at the end.</b>
    /// <para>
    /// <c>FileRepository</c>'s two name lookups filter with <c>.ToLower()</c> on a COLUMN. That is not a
    /// stylistic choice: the live database stores mixed-case names and paths (<c>Public/</c>, <c>Users/1/</c>,
    /// <c>logo-white.png</c>) while <c>Pages/Files.cshtml.cs</c> lowercases the requested URL segment before
    /// looking it up, so the column-side fold is what makes a file resolvable at all.
    /// </para>
    /// <para>
    /// The relational providers translate it. <b>RavenDB throws</b> —
    /// <c>NotSupportedException: Cannot understand how to translate method 'ToLower' of 'System.String'
    /// type</c> — and it is asserted here rather than described, because a described limitation decays into
    /// folklore and a measured one does not. Note the failure mode is LOUD, which makes it better than the -1 fold's
    /// that returned 0 of 2 rows silently and is why this could be caught before shipping rather than after.
    /// </para>
    /// <para>
    /// <b>The File cluster was moved anyway, and this test still passes — which is the point.</b> The gap
    /// measured here is REAL and was never closed: <c>.ToLower()</c> still does not translate on RavenDB, and
    /// plain <c>==</c> still means two different things. The move routed AROUND it by normalizing the data
    /// (see <see cref="A_normalized_column_filter_is_identical_on_every_provider"/>) rather than teaching
    /// <c>RavenLinqRewriting</c> to fold <c>.ToLower()</c> — the option left open earlier, rejected because its
    /// equivalence silently depends on every caller passing a lowercase right-hand side, which the seam cannot
    /// see or enforce. So this stays as the standing measurement of a live provider gap. If it ever starts
    /// failing, the gap itself has moved and that is worth knowing regardless.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seam_cannot_express_a_case_insensitive_column_filter_portably()
    {
        var siteId = await NewSiteId();
        await NewFolders().AddAsync(NewFolder(siteId, "Public/")); // stored MIXED case, as the live database does

        await using var session = FrameworkSessions.OpenQuerySession();

        var caseInsensitive = Record.Exception(() => session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.Path.ToLower() == "public/")
            .ToList());

        if (caseInsensitive is null)
        {
            // The relational half: the fold happens in the database and finds the mixed-case row.
            var found = session.Query<FolderAggregate>()
                .Where(item => item.SiteId == siteId && item.Path.ToLower() == "public/")
                .ToList();
            Assert.Single(found);
        }
        else
        {
            // The RavenDB half: a hard, loud translation failure — never a wrong answer.
            Assert.Contains("ToLower", caseInsensitive.ToString());
        }

        // ...and this is why "just drop the ToLower" is not the fix: the portable expression means DIFFERENT
        // things on the two providers. Relational equality is case-SENSITIVE (no match); RavenDB's is
        // case-INSENSITIVE (match). One expression, two semantics — measured on both, asserted on neither
        // side's assumption.
        var plainEquality = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.Path == "public/")
            .ToList();

        Assert.Equal(caseInsensitive is null ? 0 : 1, plainEquality.Count);
    }

    /// <summary>
    /// <b>THE MEASUREMENT THAT LET THE FILE CLUSTER ONTO THE SEAM, run against both providers. This is
    /// the load-bearing evidence for the whole decision.</b>
    /// <para>
    /// The test above proves the seam has no portable way to SAY "match case-insensitively". The answer was
    /// not to invent one — it was to make the question unnecessary by normalizing the DATA, exactly as ASP.NET
    /// Identity does with <c>NormalizedUserName</c>. Store a lowercase copy; filter it with the plain
    /// <c>==</c> that every provider already agrees on.
    /// </para>
    /// <para>
    /// <b>Why the two providers must agree here even though their <c>==</c> does not mean the same thing.</b>
    /// EF Core's string equality is ordinal (case-SENSITIVE); RavenDB's is case-INSENSITIVE. Those relations
    /// COINCIDE on lowercase operands — for two already-lowercase strings there is no case to be sensitive to.
    /// So normalizing both sides collapses the divergence instead of papering over it. That is the argument,
    /// and this test is where it stops being an argument: the identical assertions below run against a live
    /// SQLite and a live RavenDB, and the suite fails if either disagrees.
    /// </para>
    /// <para>
    /// Note the writes go through the STORE with aggregates built by hand — no <c>FolderMapper</c>, no
    /// <c>FileMapper</c>. If the normalization lived in the mappers (the obvious place, and the wrong one) the
    /// normalized columns would be NULL here and every assertion below would find nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_normalized_column_filter_is_identical_on_every_provider()
    {
        var siteId = await NewSiteId();

        // Stored MIXED case, as the live database does: folder 'Public/', file 'Test-Image.PNG'.
        var mixedCaseFolder = NewFolder(siteId, "Public/");
        await NewFolders().AddAsync(mixedCaseFolder);
        await NewFiles().AddAsync(NewFile(mixedCaseFolder.Id, "Test-Image.PNG"));

        await using var session = FrameworkSessions.OpenQuerySession();

        // --- the folder half of FileRepository.GetFolderIdsByPathAsync, verbatim ---
        // The render path lowercased 'Public/' out of the URL into 'public/'. Before the normalized column this was
        // item.Path.ToLower() == folderPath, which the test above shows RavenDB throws on.
        var folders = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.PathNormalized == "public/")
            .ToList();

        Assert.Single(folders);
        Assert.Equal("Public/", folders[0].Path); // the STORED case survives — normalized is a lookup key, not a replacement

        // --- the file half of FileRepository.GetFileAsync(siteId, folderPath, fileName), verbatim ---
        var folderIds = folders.ConvertAll(item => item.Id);
        var file = session.Query<FileAggregate>()
            .FirstOrDefault(item => folderIds.Contains(item.FolderId) && item.NameNormalized == "test-image.png");

        Assert.NotNull(file);
        Assert.Equal("Test-Image.PNG", file.Name); // likewise: what is displayed and what names it on disk

        // --- AND THE LIMIT OF THE GUARANTEE, MEASURED RATHER THAN ASSUMED ---
        //
        // The column alone does NOT make the providers agree. It makes them agree only when BOTH SIDES are
        // normalized. Hand this same column an argument that is not lowercase and the original divergence
        // is back, in full: EF Core's ordinal == misses, RavenDB's case-insensitive == HITS. Same expression,
        // same data, two answers.
        //
        // This is the whole reason arguments are normalized INSIDE FileRepository instead of asking callers to
        // pre-lowercase. A caller convention would be exactly the invisible, unenforceable rule that got the
        // RavenLinqRewriting option rejected — and it had already been broken in a real deployment, by
        // SynchronizationJob.ResolveFileIdAsync. Observed here, first run, not reasoned about.
        var unnormalizedArgument = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.PathNormalized == "Public/")
            .ToList();

        var ordinalEquality = unnormalizedArgument.Count == 0;
        Assert.Equal(ProviderStringEqualityIsOrdinal, ordinalEquality);
    }

    // --- SearchContent ---

    /// <summary>
    /// <b>THE LOAD-BEARING TEST OF THE WHOLE CLUSTER.</b> <c>SearchContentRepository
    /// .GetSearchContentsAsync</c>, through <see cref="ISearchQueryStore"/> — the read whose two
    /// implementations are deliberately NOT alike, so this is the only place their agreement is checked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The EF half runs the harvested query: a two-level join, <c>GROUP BY</c> over every SearchContent column
    /// and <c>Sum(SearchContentWord.Count)</c>, all in SQL. The RavenDB half runs a query-time FACET
    /// (<c>AggregateBy(...).SumOn(...)</c>) over a static <c>LoadDocument</c> index. Different mechanisms,
    /// and the assertions below are identical — which is the entire claim this section makes.
    /// </para>
    /// <para>
    /// <b>The data is shaped to catch the specific ways this can go wrong.</b> Content 1 holds
    /// <c>running</c>×3 + <c>runner</c>×2 and content 2 holds <c>run</c>×5, so both score 5 — a deliberate TIE
    /// that a mechanism ranking by relevance rather than by summed term frequency (a full-text index, say)
    /// would not reproduce. Content 3 scores 1, so it cannot be confused with them. <c>banana</c> is indexed
    /// against content 2 and must NOT be summed into a <c>run</c> search — the arm that catches a reduce key
    /// summing every word in the document, which is what a MAP-REDUCE index would have done. And a fourth
    /// content sits on ANOTHER SITE with the same word, which catches a facet that ignores its own
    /// <c>Where</c>.
    /// </para>
    /// <para>
    /// Order is deliberately NOT asserted: the harvested query has no <c>OrderBy</c> and <c>SearchService</c>
    /// sorts and pages in memory over the returned set, so row order is not observable and cannot diverge.
    /// What must match is the SET and the per-content <c>Count</c>.
    /// </para>
    /// </remarks>
    /// <summary>
    /// A query that sets no dates - an API caller's, or code's that builds a <c>SearchQuery</c> with only its keywords -
    /// finds what is indexed. <c>ToDate</c> defaults to <c>DateTime.MinValue</c>, and the date arm applied "on or before"
    /// it, so such a query found nothing on either provider.
    /// </summary>
    [Fact]
    public async Task A_search_with_no_dates_set_finds_what_is_indexed()
    {
        var siteId = await NewSiteId();
        var word = await NewWordId("undated");
        var content = await NewContentId(siteId, "Page", "undated");
        await IndexWord(content, word, 1);

        var results = await NewSearchQueries().SearchAsync(new SearchQuery { SiteId = siteId, Keywords = "undated", PageIndex = 0, PageSize = int.MaxValue });

        Assert.Contains(results, item => item.SearchContentId == content);
    }

    [Fact]
    public async Task The_search_read_sums_the_matched_term_frequencies_per_content()
    {
        var siteId = await NewSiteId();
        var otherSiteId = await NewSiteId();

        var running = await NewWordId("running");
        var runner = await NewWordId("runner");
        var run = await NewWordId("run");
        var banana = await NewWordId("banana");

        var first = await NewContentId(siteId, "Page", "alpha");
        var second = await NewContentId(siteId, "Page", "beta");
        var third = await NewContentId(siteId, "File", "gamma");
        var elsewhere = await NewContentId(otherSiteId, "Page", "othersite");

        await IndexWord(first, running, 3);
        await IndexWord(first, runner, 2);   // first  -> 5
        await IndexWord(second, run, 5);     // second -> 5 (a deliberate TIE with first)
        await IndexWord(second, banana, 7);  // must NOT be summed into a "run" search
        await IndexWord(third, running, 1);  // third  -> 1
        await IndexWord(elsewhere, running, 99); // another site: excluded by the site arm

        var results = await NewSearchQueries().SearchAsync(NewSearchQuery(siteId, "run"));

        var counts = results.ToDictionary(item => item.SearchContentId, item => item.Count);

        Assert.Equal(3, counts.Count);
        Assert.Equal(5, counts[first]);   // running x3 + runner x2 — the prefix matched BOTH words
        Assert.Equal(5, counts[second]);  // run x5, and banana x7 was NOT added to it
        Assert.Equal(1, counts[third]);
        Assert.DoesNotContain(elsewhere, counts.Keys);

        // The projected columns come back too — the harvested query selected all thirteen.
        var alpha = results.Single(item => item.SearchContentId == first);
        Assert.Equal("alpha", alpha.Title);
        Assert.Equal(siteId, alpha.SiteId);
        Assert.Equal("Page", alpha.EntityName);
    }

    /// <summary>
    /// The entity include/exclude arms of <c>GetSearchContentsAsync</c>. On EF Core these become a
    /// <c>HAVING ... IN (...)</c>; on RavenDB they filter the index entry's <c>EntityName</c>, which
    /// <c>LoadDocument</c> carried across from the SearchContent document. Both stay in the store.
    /// </summary>
    [Fact]
    public async Task The_search_read_filters_by_included_and_excluded_entities()
    {
        var siteId = await NewSiteId();
        var running = await NewWordId("running");
        var page = await NewContentId(siteId, "Page", "a-page");
        var file = await NewContentId(siteId, "File", "a-file");

        await IndexWord(page, running, 1);
        await IndexWord(file, running, 1);

        var included = await NewSearchQueries().SearchAsync(NewSearchQuery(siteId, "run", includeEntities: "Page"));
        Assert.Equal([page], included.Select(item => item.SearchContentId).ToList());

        var excluded = await NewSearchQueries().SearchAsync(NewSearchQuery(siteId, "run", excludeEntities: "Page"));
        Assert.Equal([file], excluded.Select(item => item.SearchContentId).ToList());
    }

    /// <summary>
    /// The <c>SearchQuery.Properties</c> arm — <b>the one that THREW on EF Core before this cluster
    /// moved</b>, and the reason "keep today's query verbatim" was not an option.
    /// </summary>
    /// <remarks>
    /// The harvested code expressed it two ways and both were untranslatable: <c>.Include(item =&gt;
    /// item.SearchContentProperties)</c> applied AFTER a <c>GroupBy().Select()</c> projection
    /// ("Include has been applied after 'Select'"), and <c>item.SearchContentProperties.Any(...)</c> on the
    /// projected entity ("could not be translated"). Nothing in-tree populates <c>Properties</c>, but
    /// <c>SearchResultsController.Post</c> binds it <c>[FromBody]</c>, so a client that posted one got a 500.
    /// Measured on SQLite before the change; asserted on both providers after it.
    /// </remarks>
    [Fact]
    public async Task The_properties_arm_filters_and_no_longer_throws()
    {
        var siteId = await NewSiteId();
        var running = await NewWordId("running");
        var red = await NewContentId(siteId, "Page", "red-page");
        var blue = await NewContentId(siteId, "Page", "blue-page");

        await IndexWord(red, running, 1);
        await IndexWord(blue, running, 1);

        await NewSearchContentProperties().AddAsync(new SearchContentPropertyAggregate
        { SearchContentId = red, Name = "colour", Value = "red" });
        await NewSearchContentProperties().AddAsync(new SearchContentPropertyAggregate
        { SearchContentId = blue, Name = "colour", Value = "blue" });

        var query = NewSearchQuery(siteId, "run");
        query.Properties.Add("colour", "red");

        var results = await NewSearchQueries().SearchAsync(query);

        var match = Assert.Single(results);
        Assert.Equal(red, match.SearchContentId);

        // The .Include() the harvested code could not apply: the survivor's properties come back populated.
        Assert.Equal("red", Assert.Single(match.SearchContentProperties).Value);
    }

    /// <summary>
    /// An empty keyword set returns nothing on every provider. <c>FilterByItems</c> composes
    /// <c>Expression.Constant(false)</c> for an empty list, which the relational providers turn into
    /// <c>WHERE 1=0</c>; the RavenDB half short-circuits rather than issuing a query it would have to spell
    /// differently. Asserted because the two halves reach the same answer by different routes.
    /// </summary>
    [Fact]
    public async Task The_search_read_matches_nothing_without_keywords()
    {
        var siteId = await NewSiteId();
        var running = await NewWordId("running");
        var content = await NewContentId(siteId, "Page", "alpha");
        await IndexWord(content, running, 3);

        Assert.Empty(await NewSearchQueries().SearchAsync(NewSearchQuery(siteId, "")));
    }

    // --- SearchContent: the two measurements that kept the cluster OFF the seam ---

    /// <summary>
    /// <b>THE MEASUREMENT THAT KEEPS THE SEARCHCONTENT CLUSTER OFF THE SEAM, run against both
    /// providers.</b>
    /// <para>
    /// <c>SearchContentRepository.GetSearchContentsAsync</c> is a two-level join that <c>GroupBy</c>s every
    /// SearchContent column and projects <c>Sum(SearchContentWord.Count)</c> — the per-content term
    /// frequency that becomes <c>SearchResult.Score</c> and drives the relevance sort. The relational
    /// providers translate it to a real <c>GROUP BY ... SUM(...)</c>. <b>RavenDB cannot express it at all</b>:
    /// its LINQ provider rejects an ad-hoc grouped aggregation, over a collection AND over a static index
    /// (<c>InvalidQueryException: Field 'Word' is neither an aggregation operation nor part of the group by
    /// key</c> / <c>NotSupportedException: Could not understand expression</c>). RavenDB's own answer is a
    /// map-reduce index, whose reduce key is fixed at index definition time and therefore cannot depend on
    /// the caller's keywords — and the sum here is over the MATCHED words only.
    /// </para>
    /// <para>
    /// Asserted rather than described, for the reason given earlier: a described limitation decays into folklore
    /// and a measured one does not. The failure is LOUD, never a wrong answer. <b>If this ever starts failing
    /// on RavenDB, the blocker has lifted and the cluster can cross</b> — which is exactly what this test is
    /// for.
    /// </para>
    /// <para>
    /// <b>THE SHAPE HERE IS EXACT, AND THAT IS THE WHOLE TEST.</b> RavenDB DOES support a dynamic
    /// <c>GroupBy</c>+<c>Sum</c> — but only while every <c>Where</c> arm filters a field that is part of the
    /// GROUP BY KEY. Filter on a field OUTSIDE the key and it rejects the query. That is precisely the search
    /// read: it filters <c>SearchWord.Word</c> (a prefix match, not in the key) and groups by the SearchContent
    /// columns. So the filter below deliberately touches a NON-key field — <c>IPAddress</c> — while grouping by
    /// <c>SiteId</c>. An earlier draft of this test filtered on <c>SiteId</c> and grouped by <c>SiteId</c>;
    /// RavenDB served it happily, both halves took the relational branch, and the test passed while proving
    /// nothing. It was caught by mutation check, not by reading. Do not "simplify" the filter back onto the key.
    /// </para>
    /// <para>
    /// It uses <c>VisitorAggregate</c> because the shape is what is under test, not the cluster: Visitor is
    /// already on the seam and carries the same ingredients (an <c>int</c> grouping key, an <c>int</c> measure
    /// and a string field outside the key). SearchContent has no aggregate precisely BECAUSE of this
    /// measurement.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seam_cannot_express_a_grouped_aggregation_portably()
    {
        var siteId = await NewSiteId();
        await NewVisitors().AddAsync(NewVisitor(siteId, "1.1.1.1", DateTime.UtcNow));
        await NewVisitors().AddAsync(NewVisitor(siteId, "1.2.2.2", DateTime.UtcNow));
        await NewVisitors().AddAsync(NewVisitor(siteId, "9.9.9.9", DateTime.UtcNow)); // filtered OUT

        await using var session = FrameworkSessions.OpenQuerySession();

        // The search read's shape: an arm on a field OUTSIDE the group-by key, projected into an object the
        // way GetSearchContentsAsync projects into SearchContent. See the remarks.
        static List<int> Grouped(IQuerySession session, int siteId) => session.Query<VisitorAggregate>()
            .Where(item => item.SiteId == siteId && item.IPAddress.StartsWith("1."))
            .GroupBy(item => item.SiteId)
            .Select(group => new { SiteId = group.Key, Visits = group.Sum(item => item.Visits) })
            .ToList()
            .ConvertAll(item => item.Visits);

        var grouped = Record.Exception(() => Grouped(session, siteId));

        if (grouped is null)
        {
            // The relational half: the aggregation happens in the database, which is where the EF
            // implementation to keep it. Two of the three rows match the non-key arm; NewVisitor sets Visits = 1.
            Assert.Equal(2, Assert.Single(Grouped(session, siteId)));
        }
        else
        {
            // The RavenDB half: a hard, loud rejection of the shape — not a silently different answer. The
            // exact exception depends on the projection (InvalidQueryException names the offending field;
            // a bare scalar projection yields NotSupportedException), so both are accepted — what is asserted
            // is that it FAILS rather than answering differently.
            Assert.True(
                grouped.GetType().Name is "InvalidQueryException" or nameof(NotSupportedException),
                $"expected a loud translation failure, got {grouped.GetType().Name}: {grouped.Message}");
        }
    }

    /// <summary>
    /// <b>The HONEST NEGATIVE that goes with the test above, run against both providers.</b>
    /// <para>
    /// The keyword arm of the search read — <c>SearchWord.Word.StartsWith(keyword)</c>, composed into an OR
    /// chain by <c>QueryableExtensions.FilterByItems</c>'s expression tree — was expected to be the portability
    /// problem, following the earlier <c>.ToLower()</c> finding. <b>It is not.</b> Both providers translate <c>StartsWith</c>
    /// and both keep it in the store (EF Core emits <c>LIKE 'run%'</c>). So the prefix arm is NOT what blocks
    /// the cluster; the grouped aggregation above is. Recorded because a negative that is assumed decays the
    /// same way a limitation that is described does.
    /// </para>
    /// <para>
    /// <b>The latent hazard it also pins, and it is WIDER than the earlier finding.</b> <c>StartsWith</c>'s case semantics
    /// diverge THREE ways, not two, and <see cref="ProviderStringEqualityIsOrdinal"/> does NOT describe them —
    /// that property is about <c>==</c>, a different operator. EF Core translates <c>StartsWith</c> to
    /// <c>LIKE</c>, and <b>SQLite's <c>LIKE</c> is case-INSENSITIVE for ASCII while PostgreSQL's is
    /// case-SENSITIVE</b> (measured: <c>SELECT 'running' LIKE 'RUN%'</c> returns <c>f</c> on
    /// the PostgreSQL test server). RavenDB's is case-INSENSITIVE. So two RELATIONAL providers, which the earlier findings both
    /// treat as one behaviour class — disagree with each other here.
    /// </para>
    /// <para>
    /// <b>Which this suite structurally cannot see, and that is the finding.</b> Both EF fixtures are SQLite
    /// so the EF half reports SQLite's answer and PostgreSQL — what the product actually runs on, and
    /// what the render guard uses — is not represented. <see cref="ProviderPrefixFilterIsOrdinal"/> is
    /// therefore a fact about THIS FIXTURE, not about "EF Core".
    /// </para>
    /// <para>
    /// The search cluster does not currently depend on any of it, and that is design rather than luck, though
    /// nobody wrote it down: <c>DatabaseSearchProvider.GetWords</c> lowercases every word it indexes and
    /// <c>SearchUtils.GetKeywords</c> lowercases every keyword it queries, so both sides are already normalized
    /// and all three relations coincide — the normalizing argument, arrived at for free earlier. Anything that
    /// stores a non-lowercase <c>SearchWord.Word</c> breaks the coincidence silently, and this is where it
    /// surfaces.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_prefix_filter_is_expressible_on_every_provider()
    {
        var siteId = await NewSiteId();
        await NewFolders().AddAsync(NewFolder(siteId, "running/"));
        await NewFolders().AddAsync(NewFolder(siteId, "banana/"));

        await using var session = FrameworkSessions.OpenQuerySession();

        // THE LOAD-BEARING ASSERTION: a lowercase prefix against lowercase data — which is the only shape the
        // product ever issues — is identical on every provider, and stays in the store. This is why the
        // keyword arm is NOT what blocks the cluster.
        var prefixed = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.PathNormalized.StartsWith("run"))
            .ToList();

        Assert.Equal("running/", Assert.Single(prefixed).PathNormalized);

        // ...and the case semantics, stated out loud rather than assumed. See the remarks: this is a
        // three-way split, and this suite can only see two of the three.
        var upperCased = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.PathNormalized.StartsWith("RUN"))
            .ToList();

        Assert.Equal(ProviderPrefixFilterIsOrdinal, upperCased.Count == 0);
    }

    /// <summary>
    /// Whether this FIXTURE's <c>StartsWith</c> is ORDINAL (case-sensitive). <b>False for both</b> — SQLite's
    /// <c>LIKE</c> and RavenDB's prefix match are each case-insensitive.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT folded into <see cref="ProviderStringEqualityIsOrdinal"/>, which is true for EF Core
    /// and describes <c>==</c>. <c>StartsWith</c> is a different operator with different semantics, and on the
    /// relational side it is the DATABASE's <c>LIKE</c> rule rather than EF Core's: PostgreSQL would answer
    /// <c>true</c> here where SQLite answers <c>false</c>. Both EF fixtures are SQLite, so that case is
    /// unrepresented — naming the property after the fixture rather than the provider is what keeps it from
    /// reading as a guarantee about EF Core that it is not.
    /// </remarks>
    protected abstract bool ProviderPrefixFilterIsOrdinal { get; }

    /// <summary>
    /// Whether this provider's string <c>==</c> is ORDINAL (case-sensitive) — true for EF Core, false for
    /// RavenDB, which compares case-insensitively by default.
    /// </summary>
    /// <remarks>
    /// Declared as a provider fact rather than discovered per test, because it is one: it is the divergence
    /// measured, it applies to EVERY string equality on this seam (Page.Path, User.Username,
    /// UrlMapping.Url), and the normalizing change neither introduced nor fixed it. What it did was stop DEPENDING on it, by
    /// normalizing both sides so the two relations coincide. This property exists so that the one test which
    /// probes the edge of that guarantee can state the difference out loud instead of asserting one provider's
    /// behaviour and skipping on the other.
    /// </remarks>
    protected abstract bool ProviderStringEqualityIsOrdinal { get; }

    /// <summary>
    /// The cardinality the normalizing change promised to preserve: the <c>(site_id, path)</c> unique index is case-SENSITIVE and
    /// was deliberately NOT moved onto the normalized column, so <c>Docs/</c> and <c>docs/</c> may still
    /// coexist — and a lookup for <c>docs/</c> must therefore return BOTH.
    /// </summary>
    /// <remarks>
    /// This is why <c>FileRepository.GetFolderIdsByPathAsync</c> returns a LIST and fans in on
    /// <c>FolderId IN (...)</c> rather than picking one. Returning the first would silently change which file
    /// a URL resolves to. Asserted on both providers, though note the index itself is a relational constraint
    /// with no RavenDB equivalent (named on <c>RavenFolderStore</c>) — what is asserted here is the READ's
    /// cardinality, which both must agree on.
    /// </remarks>
    [Fact]
    public async Task A_normalized_lookup_returns_every_row_the_case_sensitive_index_permits()
    {
        var siteId = await NewSiteId();

        await NewFolders().AddAsync(NewFolder(siteId, "Docs/"));
        await NewFolders().AddAsync(NewFolder(siteId, "docs/"));

        await using var session = FrameworkSessions.OpenQuerySession();

        var found = session.Query<FolderAggregate>()
            .Where(item => item.SiteId == siteId && item.PathNormalized == "docs/")
            .ToList();

        Assert.Equal(2, found.Count);
        Assert.Equal(["Docs/", "docs/"], found.ConvertAll(item => item.Path).OrderBy(path => path, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The normalized column tracks its source through an UPDATE, on both providers — a rename from
    /// <c>Old-Name.PNG</c> to <c>New-Name.PNG</c> must make the file findable by the new normalized name and
    /// NOT by the old one.
    /// </summary>
    /// <remarks>
    /// The derivation lives in <see cref="FileAggregate.Name"/>'s setter, so this is really asking whether
    /// both providers persist a property that has no public setter of its own. EF writes it because it maps it
    /// as an ordinary property; RavenDB writes it because its serializer round-trips the public getter. Worth
    /// asserting rather than assuming: a provider that silently dropped the column would leave the file
    /// findable only by its OLD name, which no compiler and no unit test would notice.
    /// </remarks>
    [Fact]
    public async Task A_renamed_file_is_found_by_its_new_normalized_name_on_every_provider()
    {
        var siteId = await NewSiteId();
        var folder = NewFolder(siteId, "Public/");
        await NewFolders().AddAsync(folder);

        var file = NewFile(folder.Id, "Old-Name.PNG");
        await NewFiles().AddAsync(file);

        // The read-through-a-session, write-through-the-store shape the repository uses.
        FileAggregate detached;
        await using (var read = FrameworkSessions.OpenQuerySession())
        {
            detached = read.Query<FileAggregate>().First(item => item.Id == file.Id);
            Assert.Equal("old-name.png", detached.NameNormalized); // it round-tripped OUT of the provider, not just into it
        }

        detached.Name = "New-Name.PNG";
        await NewFiles().UpdateAsync(detached);

        await using var session = FrameworkSessions.OpenQuerySession();

        Assert.NotNull(session.Query<FileAggregate>().FirstOrDefault(item => item.NameNormalized == "new-name.png"));
        Assert.Null(session.Query<FileAggregate>().FirstOrDefault(item => item.NameNormalized == "old-name.png"));
    }

    // --- id generation, per cluster ---

    /// <summary>
    /// The blind-upsert destruction test (src/Stomme.Persistence.RavenDb/README.md), applied to EVERY cluster rather than to one
    /// synthetic aggregate. The framework store inserts with <c>Id = 0</c> and expects the database to assign
    /// one; a provider that does not addresses every insert as <c>{Type}/0</c> and silently overwrites the
    /// previous document.
    /// <para>
    /// Asserts DOCUMENT COUNT as well as distinct ids, and the count assertion is the one that matters: it
    /// stays reachable on broken code (an id assertion fails at its first assert and never reaches the
    /// survival check) and reports "Expected: 2, Actual: 1" — two inserts, one surviving row, no exception.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Two_inserts_into_every_cluster_get_distinct_ids_and_both_survive()
    {
        await AssertTwoSurvive(
            () => NewSite("site-" + Guid.NewGuid().ToString("N")),
            NewSites);
        await AssertTwoSurvive(() => NewPage(4242, "/p-" + Guid.NewGuid().ToString("N")), NewPages);
        await AssertTwoSurvive(() => NewModule(4242), NewModules);
        await AssertTwoSurvive(() => NewUser("u-" + Guid.NewGuid().ToString("N")), NewUsers);
        await AssertTwoSurvive(() => NewRole(4242, "r-" + Guid.NewGuid().ToString("N")), NewRoles);
        await AssertTwoSurvive(() => NewUserRole(4242, 4242), NewUserRoles);
        await AssertTwoSurvive(() => NewSetting("Site", 4242, "s-" + Guid.NewGuid().ToString("N"), "v"), NewSettings);
        await AssertTwoSurvive(() => NewPermission(4242, "Page", 4242, "p-" + Guid.NewGuid().ToString("N"), roleId: 1), NewPermissions);

        // The Job/JobLog pair — the MASTER-database cluster. JobLog is the one that most needs this guard:
        // the scheduler INSERTS a log when a run starts and reads its assigned id straight back to UPDATE the
        // FinishDate when the run ends, so an id generator that addressed every insert as JobLog/0 would keep
        // exactly one log per database and update the wrong row every tick. Each Job gets a distinct type only
        // so the two rows are visibly distinct in a failure report — Job has no unique index on it.
        await AssertTwoSurvive(() => NewJob("Stomme.Jobs.Job" + Guid.NewGuid().ToString("N") + ", Stomme.Server"), NewJobs);
        await AssertTwoSurvive(() => NewJobLog(Interlocked.Increment(ref _jobLogJobId)), NewJobLogs);

        // The five leaf clusters. Log and Notification are the two that most need this: both are
        // append-heavy and neither is IAuditable, so an id generator that addressed every insert as {Type}/0
        // would silently keep exactly one log entry and one notification per database.
        await AssertTwoSurvive(() => NewLanguage(4242, "l-" + Guid.NewGuid().ToString("N"), isDefault: false), NewLanguages);
        await AssertTwoSurvive(() => NewLog(4242, "Error", DateTime.UtcNow), NewLogs);
        await AssertTwoSurvive(() => NewProfile(4242, "p-" + Guid.NewGuid().ToString("N")), NewProfiles);
        await AssertTwoSurvive(() => NewSiteTask(4242, "t-" + Guid.NewGuid().ToString("N"), isCompleted: false, createdOn: DateTime.UtcNow), NewSiteTasks);
        await AssertTwoSurvive(() => NewNotification(4242, "n-" + Guid.NewGuid().ToString("N")), NewNotifications);

        // The SiteGroup pair. The MEMBER is the one that needs this guard most: SiteGroupMember carries a
        // UNIQUE index on (SiteId, SiteGroupId) on the relational providers and nothing equivalent on RavenDB,
        // so a blind upsert to SiteGroupMember/0 is exactly the failure this suite exists to catch — every
        // site's membership silently collapsing onto one document, with the synchronization job then seeing a
        // single member per tenant. Each member below gets a DISTINCT SiteId so the relational unique index is
        // not what makes the test pass.
        await AssertTwoSurvive(() => NewSiteGroup(SiteGroupTypes.Synchronization, 4242, synchronize: false), NewSiteGroups);
        await AssertTwoSurvive(() => NewSiteGroupMember(4242, Interlocked.Increment(ref _siteGroupMemberSiteId)), NewSiteGroupMembers);

        // Visitor, UrlMapping and PageModule. Visitor is the one that most needs this guard: App.razor
        // INSERTS a visitor on the render path and reads the assigned id straight into the response cookie, so
        // an id generator that addressed every insert as Visitor/0 would keep exactly one visitor per database
        // AND hand every browser a cookie for it. PageModule is a close second — it is the render path's own
        // table, and one surviving instance per database is a blank site.
        //
        // Each UrlMapping below gets a DISTINCT Url so the relational UNIQUE index on (SiteId, Url) is not what
        // makes the test pass — the same care as with SiteGroupMember's unique index.
        await AssertTwoSurvive(() => NewVisitor(4242, "10.0.0." + Interlocked.Increment(ref _visitorAddress) % 255, DateTime.UtcNow), NewVisitors);
        await AssertTwoSurvive(() => NewUrlMapping(4242, "u-" + Guid.NewGuid().ToString("N"), ""), NewUrlMappings);
        await AssertTwoSurvive(() => NewPageModule(4242, 4242, "Content", 1), NewPageModules);

        // Folder. Each folder below gets a DISTINCT path so the relational UNIQUE index on
        // (site_id, path) is not what makes the test pass — the same care as with SiteGroupMember's and
        // with UrlMapping's. A blind upsert to Folder/0 would leave one folder per database, which is a
        // site with no content directories at all: every file upload and every file read resolves through one.
        await AssertTwoSurvive(() => NewFolder(4242, "f-" + Guid.NewGuid().ToString("N") + "/"), NewFolders);
        await AssertTwoSurvive(() => NewFile(4242, "f-" + Guid.NewGuid().ToString("N") + ".png"), NewFiles);

        // The SearchContent cluster — Phase 3's last. SearchContentWord is the one that needs this most:
        // it is the highest-cardinality table in the product (one row per distinct word PER indexed document),
        // it is rewritten wholesale on every content save, and it is NOT IAuditable — so an id generator that
        // addressed every insert as SearchContentWordAggregate/0 would silently collapse the entire inverted
        // index to ONE row per database. Search would then return either nothing or one arbitrary document,
        // with no error anywhere. Each row below is distinct on the columns its UNIQUE index covers, so the
        // relational index is not what makes this pass.
        await AssertTwoSurvive(() => NewSearchContent(4242, "e-" + Guid.NewGuid().ToString("N")), NewSearchContents);
        await AssertTwoSurvive(() => NewSearchWord("w-" + Guid.NewGuid().ToString("N")), NewSearchWords);
        await AssertTwoSurvive(
            () => NewSearchContentWord(4242, Interlocked.Increment(ref _searchWordId)),
            NewSearchContentWords);
        await AssertTwoSurvive(
            () => NewSearchContentProperty(Interlocked.Increment(ref _searchPropertyContentId), "colour"),
            NewSearchContentProperties);
    }

    // Distinct ids per row created by the guard, so the relational UNIQUE indexes on
    // (search_content_id, search_word_id) and (search_content_id, name) are not what make it pass.
    private static int _searchWordId = 600000;
    private static int _searchPropertyContentId = 700000;

    // A distinct IP per visitor created by the guard. Visitor has no unique index, so this is not required
    // for correctness — it is here so the two rows are visibly distinct when the guard reports a failure.
    private static int _visitorAddress = 0;

    // A distinct SiteId per member created by the guard — see there. Static because the guard creates its
    // members through a Func the helper calls twice, and both calls must differ.
    private static int _siteGroupMemberSiteId = 500000;

    // A distinct JobId per JobLog created by the guard. JobLog has no unique index, so this is not required
    // for correctness — it is here so the two rows are visibly distinct when the guard reports a failure.
    private static int _jobLogJobId = 800000;

    private static async Task AssertTwoSurvive<TAggregate>(
        Func<TAggregate> create,
        Func<IRepository<TAggregate, int>> newStore)
        where TAggregate : AggregateRoot<int>
    {
        var before = (await newStore().GetAllAsync()).Count;

        var first = create();
        var second = create();
        await newStore().AddAsync(first);
        await newStore().AddAsync(second);

        var after = await newStore().GetAllAsync();

        // The count assertion first: it is the one that stays reachable when id generation is broken.
        Assert.Equal(before + 2, after.Count);
        Assert.NotEqual(0, first.Id);
        Assert.NotEqual(0, second.Id);
        Assert.NotEqual(first.Id, second.Id);
    }

    // --- fixtures for the aggregates under test ---
    //
    // Every factory populates the columns the harvested schema declares NOT NULL, including the audit stamps.
    // That is not busywork: the relational provider REJECTS an aggregate with a null CreatedBy (SQLite Error
    // 19), while RavenDB, being schemaless, stores it happily. Building each aggregate completely is what
    // keeps the two providers running the same test rather than two different ones. (Audit STAMPING itself is
    // conformance-tested on both providers at the seam level — see PersistenceConformanceTests; these stamps
    // are test data, not an assertion.)

    private const string Actor = "conformance-user";

    private async Task<int> NewSiteId()
    {
        var site = NewSite("site-" + Guid.NewGuid().ToString("N"));
        await NewSites().AddAsync(site);
        return site.Id;
    }

    private async Task<int> NewSiteGroupId(string type, int primarySiteId, bool synchronize = true)
    {
        var siteGroup = NewSiteGroup(type, primarySiteId, synchronize);
        await NewSiteGroups().AddAsync(siteGroup);
        return siteGroup.Id;
    }

    private async Task<int> NewRoleId(string name, int? siteId)
    {
        var role = NewRole(siteId, name);
        await NewRoles().AddAsync(role);
        return role.Id;
    }

    // A Job in the MASTER database, so JobLogs can reference a real job id. Distinct type per call.
    private async Task<int> NewJobId()
    {
        var job = NewJob("Stomme.Jobs.Job" + Guid.NewGuid().ToString("N") + ", Stomme.Server");
        await NewJobs().AddAsync(job);
        return job.Id;
    }

    private static SiteAggregate NewSite(string name) => Stamp(new SiteAggregate
    {
        Name = name,
        SiteGuid = Guid.NewGuid().ToString(),
        DefaultThemeType = "StommeTheme",
        DefaultContainerType = "StommeContainer",
        AdminContainerType = "AdminContainer",
        AdminThemeType = "AdminTheme",
        TimeZoneId = "UTC",
        CultureCode = "en-US"
    });

    private static PageAggregate NewPage(int siteId, string path) => Stamp(new PageAggregate
    {
        SiteId = siteId,
        Path = path,
        Name = path.Trim('/'),
        Title = path.Trim('/'),
        Icon = string.Empty
    });

    private static ModuleAggregate NewModule(int siteId) => Stamp(new ModuleAggregate
    {
        SiteId = siteId,
        ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client"
    });

    private static UserAggregate NewUser(string username) => Stamp(new UserAggregate
    {
        Username = username,
        DisplayName = username,
        Email = username + "@example.com",
        LastIPAddress = string.Empty
    });

    private static RoleAggregate NewRole(int? siteId, string name) => Stamp(new RoleAggregate
    {
        SiteId = siteId,
        Name = name,
        Description = name
    });

    private static UserRoleAggregate NewUserRole(int userId, int roleId) => Stamp(new UserRoleAggregate
    {
        UserId = userId,
        RoleId = roleId
    });

    private static SettingAggregate NewSetting(string entityName, int entityId, string name, string value) => Stamp(new SettingAggregate
    {
        EntityName = entityName,
        EntityId = entityId,
        SettingName = name,
        SettingValue = value
    });

    /// <summary>
    /// A ROLE permission by default (RoleId set, UserId null) — the common shape. Pass
    /// <paramref name="roleId"/> null and set UserId for the user half; a permission is granted to one or the
    /// other, never both.
    /// </summary>
    private static PermissionAggregate NewPermission(int siteId, string entityName, int entityId, string permissionName, int? roleId) =>
        Stamp(new PermissionAggregate
        {
            SiteId = siteId,
            EntityName = entityName,
            EntityId = entityId,
            PermissionName = permissionName,
            RoleId = roleId,
            IsAuthorized = true
        });

    // A Job. IAuditable, so STAMPED — the job table carries the audit quartet (unlike JobLog below). Every
    // NOT NULL column the harvested schema declares is populated; StartDate/EndDate/NextExecution stay null.
    private static JobAggregate NewJob(string jobType) => Stamp(new JobAggregate
    {
        Name = "job-" + Guid.NewGuid().ToString("N"),
        JobType = jobType,
        Frequency = "m",
        Interval = 1,
        IsEnabled = true,
        IsStarted = false,
        IsExecuting = false,
        RetentionHistory = 10,
        MaximumDuration = 30
    });

    /// <summary>
    /// A JobLog. NOT stamped: the job_log table has no audit columns, so <see cref="JobLogAggregate"/> is
    /// not <c>IAuditable</c> (like <see cref="LogAggregate"/> and <see cref="NotificationAggregate"/>). Its
    /// <c>Job</c> navigation is <c>[NotMapped]</c> view state the repository populates, absent here by design.
    /// </summary>
    private static JobLogAggregate NewJobLog(int jobId) => new()
    {
        JobId = jobId,
        StartDate = DateTime.UtcNow,
        FinishDate = null,
        Succeeded = null,
        Notes = "",
        Server = "conformance",
        Instance = ""
    };

    // A Theme. IAuditable, so STAMPED — the theme table carries the audit quartet. ThemeVersion is the
    // `version` STRING column, carried past the AggregateRoot.Version uint shadow.
    private static ThemeAggregate NewTheme(string themeName, string name, string version) => Stamp(new ThemeAggregate
    {
        ThemeName = themeName,
        Name = name,
        ThemeVersion = version
    });

    // A ModuleDefinition. IAuditable, so STAMPED — the module_definition table carries the audit quartet.
    // ModuleDefinitionVersion is the `version` STRING column, carried past the AggregateRoot.Version uint shadow.
    private static ModuleDefinitionAggregate NewModuleDefinition(string moduleDefinitionName, string name, string description, string categories, string version) => Stamp(new ModuleDefinitionAggregate
    {
        ModuleDefinitionName = moduleDefinitionName,
        Name = name,
        Description = description,
        Categories = categories,
        ModuleDefinitionVersion = version
    });

    // A Tenant. IAuditable, so STAMPED — the tenant table carries the audit quartet. DBType is the
    // provider-selector (absent from the builder); TenantVersion is the `version` STRING column, carried past the
    // AggregateRoot.Version uint shadow.
    private static TenantAggregate NewTenant(string name, string dbType, string version) => Stamp(new TenantAggregate
    {
        Name = name,
        DBConnectionString = "DefaultConnection",
        DBType = dbType,
        TenantVersion = version
    });

    // An Alias. IAuditable, so STAMPED — the alias table carries the audit quartet. IsDefault maps to the
    // NULLABLE is_default column (absent from the builder). Alias has NO version column, so nothing shadows
    // AggregateRoot.Version.
    private static AliasAggregate NewAlias(string name, int tenantId, int siteId, bool isDefault) => Stamp(new AliasAggregate
    {
        Name = name,
        TenantId = tenantId,
        SiteId = siteId,
        IsDefault = isDefault
    });

    private static SiteGroupAggregate NewSiteGroup(string type, int primarySiteId, bool synchronize) => Stamp(new SiteGroupAggregate
    {
        Name = "group-" + Guid.NewGuid().ToString("N"),
        Type = type,
        PrimarySiteId = primarySiteId,
        Synchronize = synchronize
    });

    // SynchronizedOn is left null deliberately: "never synchronized" is the state a member is created in, and
    // it is the pair's only nullable column.
    private static SiteGroupMemberAggregate NewSiteGroupMember(int siteGroupId, int siteId) => Stamp(new SiteGroupMemberAggregate
    {
        SiteGroupId = siteGroupId,
        SiteId = siteId,
        SynchronizedOn = null
    });

    private static LanguageAggregate NewLanguage(int? siteId, string code, bool isDefault) => Stamp(new LanguageAggregate
    {
        SiteId = siteId,
        Code = code,
        IsDefault = isDefault
    });

    /// <summary>
    /// NOT stamped, and that is the point: the Log table has no audit columns at all, so
    /// <see cref="LogAggregate"/> is the one framework-store aggregate that is not <c>IAuditable</c>. Its
    /// clock is <see cref="LogAggregate.LogDate"/>, supplied here as test data because it is the sort and
    /// retention key. Every column the harvested schema declares NOT NULL is still populated — the relational
    /// provider rejects a null <c>Message</c> or <c>Url</c> (SQLite Error 19) where RavenDB stores it happily,
    /// and building the aggregate completely is what keeps both providers running the same test.
    /// </summary>
    private static LogAggregate NewLog(int? siteId, string level, DateTime logDate) => new()
    {
        SiteId = siteId,
        LogDate = logDate,
        Url = "/",
        Server = "conformance",
        Category = "Stomme.Tests",
        Feature = "Conformance",
        Function = "Other",
        Level = level,
        Message = "message",
        MessageTemplate = "message"
    };

    private static ProfileAggregate NewProfile(int? siteId, string name) => Stamp(new ProfileAggregate
    {
        SiteId = siteId,
        Name = name,
        Title = name,
        Category = "Contact",
        ViewOrder = 1,
        MaxLength = 50,
        IsRequired = false,
        IsPrivate = false,
        Rows = 1
    });

    private static SiteTaskAggregate NewSiteTask(int siteId, string name, bool isCompleted, DateTime createdOn)
    {
        var aggregate = Stamp(new SiteTaskAggregate
        {
            SiteId = siteId,
            Name = name,
            Type = "Stomme.Infrastructure.SomeTask, Stomme.Server",
            IsCompleted = isCompleted
        });

        // AFTER Stamp: CreatedOn is both an audit stamp and this cluster's sort/retention key, and these tests
        // need to control it. On the relational provider DbContextUtils would overwrite it, but this fixture
        // drives EfRepository over a plain DbContext with no such override, so the value survives — the same
        // reason the other clusters' stamps here are test data rather than assertions.
        aggregate.CreatedOn = createdOn;
        return aggregate;
    }

    /// <summary>
    /// NOT stamped: the Notification table has no audit columns either (see
    /// <see cref="NotificationAggregate"/>). <c>CreatedOn</c> here is the notification's OWN column, set by
    /// the <see cref="Notification"/> constructors in production, not an audit stamp.
    /// </summary>
    private static NotificationAggregate NewNotification(int siteId, string subject) => new()
    {
        SiteId = siteId,
        ToEmail = "someone@example.com",
        Subject = subject,
        Body = "body",
        CreatedOn = DateTime.UtcNow,
        IsDelivered = false,
        IsDeleted = false,
        IsRead = false
    };

    /// <summary>
    /// NOT stamped: the Visitor table has no audit columns (see <see cref="VisitorAggregate"/>).
    /// <c>CreatedOn</c> here is the visitor's OWN column — when it was FIRST seen — and
    /// <paramref name="visitedOn"/> is the filter/sort key the report and the purge both use.
    /// </summary>
    // --- SearchContent factories ---

    /// <summary>
    /// A SearchQuery as <c>Admin/SearchResults/Index.razor</c> builds one — note <c>FromDate</c>/<c>ToDate</c>
    /// at MinValue/MaxValue, which is what the repository's date arms test against to decide whether to apply
    /// themselves at all.
    /// </summary>
    private static SearchQuery NewSearchQuery(
        int siteId,
        string keywords,
        string includeEntities = "",
        string excludeEntities = "") => new()
        {
            SiteId = siteId,
            Keywords = keywords,
            IncludeEntities = includeEntities,
            ExcludeEntities = excludeEntities,
            FromDate = DateTime.MinValue,
            ToDate = DateTime.MaxValue,
            PageIndex = 0,
            PageSize = int.MaxValue
        };

    /// <summary>
    /// A word in the dictionary. ALWAYS LOWERCASE — see <c>SearchWordAggregate</c>: both sides of the prefix
    /// filter being lowercase is what makes SQLite's case-insensitive LIKE, PostgreSQL's case-sensitive LIKE
    /// and RavenDB's case-insensitive prefix match all agree.
    /// </summary>
    private async Task<int> NewWordId(string word)
    {
        var aggregate = new SearchWordAggregate { Word = word, CreatedOn = DateTime.UtcNow };
        await NewSearchWords().AddAsync(aggregate);
        return aggregate.Id;
    }

    private async Task<int> NewContentId(int siteId, string entityName, string title)
    {
        var aggregate = new SearchContentAggregate
        {
            SiteId = siteId,
            EntityName = entityName,
            EntityId = Guid.NewGuid().ToString("N"),
            Title = title,
            Description = "",
            Body = "",
            Url = "/" + title,
            Permissions = "All Users",
            ContentModifiedBy = "conformance",
            ContentModifiedOn = DateTime.UtcNow,
            AdditionalContent = "",
            CreatedOn = DateTime.UtcNow
        };

        await NewSearchContents().AddAsync(aggregate);
        return aggregate.Id;
    }

    /// <summary>One term-frequency row: "this word occurs N times in this content".</summary>
    private async Task IndexWord(int searchContentId, int searchWordId, int count) =>
        await NewSearchContentWords().AddAsync(new SearchContentWordAggregate
        {
            SearchContentId = searchContentId,
            SearchWordId = searchWordId,
            Count = count,
            CreatedOn = DateTime.UtcNow,
            ModifiedOn = DateTime.UtcNow
        });

    private static SearchContentAggregate NewSearchContent(int siteId, string entityId) => new()
    {
        SiteId = siteId,
        EntityName = "Page",
        EntityId = entityId,
        Title = "t",
        Description = "",
        Body = "",
        Url = "/" + entityId,
        Permissions = "All Users",
        ContentModifiedBy = "conformance",
        ContentModifiedOn = DateTime.UtcNow,
        AdditionalContent = "",
        CreatedOn = DateTime.UtcNow
    };

    private static SearchWordAggregate NewSearchWord(string word) => new()
    {
        Word = word,
        CreatedOn = DateTime.UtcNow
    };

    private static SearchContentWordAggregate NewSearchContentWord(int searchContentId, int searchWordId) => new()
    {
        SearchContentId = searchContentId,
        SearchWordId = searchWordId,
        Count = 1,
        CreatedOn = DateTime.UtcNow,
        ModifiedOn = DateTime.UtcNow
    };

    private static SearchContentPropertyAggregate NewSearchContentProperty(int searchContentId, string name) => new()
    {
        SearchContentId = searchContentId,
        Name = name,
        Value = "v"
    };

    private static VisitorAggregate NewVisitor(int siteId, string ipAddress, DateTime visitedOn) => new()
    {
        SiteId = siteId,
        UserId = null, // anonymous — the common case, and the cluster's only nullable reference
        Visits = 1,
        IPAddress = ipAddress,
        UserAgent = "conformance",
        Language = "en-US",
        Url = "/",
        Referrer = "",
        CreatedOn = visitedOn,
        VisitedOn = visitedOn
    };

    /// <summary>
    /// NOT stamped: the UrlMapping table has no audit columns either. Pass <paramref name="mappedUrl"/> empty
    /// for a captured broken link and non-empty for a redirect — that distinction IS the cluster's read
    /// predicate.
    /// </summary>
    private static UrlMappingAggregate NewUrlMapping(int siteId, string url, string mappedUrl) => new()
    {
        SiteId = siteId,
        Url = url,
        MappedUrl = mappedUrl,
        Requests = 1,
        Referrer = null,
        CreatedOn = DateTime.UtcNow,
        RequestedOn = DateTime.UtcNow
    };

    /// <summary>
    /// Stamped, and IsDeleted set explicitly: PageModule is the one aggregate on the seam that is BOTH
    /// IAuditable and IDeletable, so its table declares the audit quartet NOT NULL (which the relational
    /// provider enforces) and is_deleted NOT NULL as well.
    /// </summary>
    private static PageModuleAggregate NewPageModule(int pageId, int moduleId, string pane, int order)
    {
        var aggregate = Stamp(new PageModuleAggregate
        {
            PageId = pageId,
            ModuleId = moduleId,
            Title = "Conformance Module",
            Pane = pane,
            Order = order,
            ContainerType = "Stomme.Themes.Controls.Container.DefaultContainer, Stomme.Client",
            IsDeleted = false
        });

        return aggregate;
    }

    /// <summary>
    /// Stamped: <see cref="FolderAggregate"/> IS IAuditable and its table declares the audit quartet NOT NULL.
    /// It is NOT IDeletable, despite FolderEntityBuilder creating the three deletable columns — three
    /// migrations take them away again and the live database has none of them. See
    /// <see cref="FolderAggregate"/>.
    /// </summary>
    private static FolderAggregate NewFolder(int siteId, string path) => Stamp(new FolderAggregate
    {
        SiteId = siteId,
        ParentId = null,
        Type = "Private",
        Name = path.Trim('/') is { Length: > 0 } name ? name : "Root",
        Path = path,
        Order = 1,
        IsSystem = false
    });

    /// <summary>
    /// <see cref="FileAggregate"/>. Note nothing here sets <see cref="FileAggregate.NameNormalized"/> — it has
    /// no public setter, because <see cref="FileAggregate.Name"/> derives it. That is exactly the
    /// property under test: this factory is a store write that BYPASSES <c>FileMapper</c>, which is the way in
    /// a normalization living in the mapper would have missed.
    /// </summary>
    private static FileAggregate NewFile(int folderId, string name) => Stamp(new FileAggregate
    {
        FolderId = folderId,
        Name = name,
        Extension = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..].ToLowerInvariant() : "bin",
        Size = 8192,
        ImageHeight = 80,
        ImageWidth = 250
    });

    /// <summary>Fills the four audit columns the harvested schema declares NOT NULL.</summary>
    /// <remarks>
    /// Only for aggregates that HAVE them. <see cref="LogAggregate"/> and <see cref="NotificationAggregate"/>
    /// do not — their tables carry no audit columns — so their factories above build the aggregate directly
    /// rather than calling this. Passing either here would throw on the first <c>GetProperty("CreatedBy")!</c>.
    /// </remarks>
    private static TAggregate Stamp<TAggregate>(TAggregate aggregate)
    {
        var now = DateTime.UtcNow;
        var type = typeof(TAggregate);
        type.GetProperty("CreatedBy")!.SetValue(aggregate, Actor);
        type.GetProperty("ModifiedBy")!.SetValue(aggregate, Actor);
        type.GetProperty("CreatedOn")!.SetValue(aggregate, now);
        type.GetProperty("ModifiedOn")!.SetValue(aggregate, now);
        return aggregate;
    }
}
