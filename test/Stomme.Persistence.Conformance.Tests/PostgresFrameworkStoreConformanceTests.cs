using Stomme.Database.PostgreSQL;
using Stomme.Databases.Interfaces;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The framework-store conformance suite against the EF Core provider ON POSTGRESQL — the database
/// the live product runs on, and the one whose answers the SQLite fixture
/// (<see cref="EfCoreFrameworkStoreConformanceTests"/>) structurally cannot represent: that fixture's own
/// <c>ProviderPrefixFilterIsOrdinal</c> remarks measured PostgreSQL answering <c>true</c> where SQLite
/// answers <c>false</c>, and until this fixture existed that answer was in a comment, not in the suite.
/// <para>
/// <b>Two scratch PostgreSQL databases per test, mirroring the two contexts</b> — the same two-connection
/// shape as the SQLite twin, on the shared test server (<c>stomme_conformance_fst_*</c> tenant,
/// <c>stomme_conformance_fsm_*</c> master), force-dropped in teardown, stale leftovers swept at
/// initialization (see <see cref="PostgresConformanceEndpoint"/>). FAILS, never skips, when PostgreSQL is
/// unreachable; <see cref="PostgresParityCoverageTests"/> is the named diagnosis.
/// </para>
/// </summary>
/// <remarks>
/// <b>The schema is the SQLite fixture's hand-written DDL TRANSLATED, not re-derived.</b> The column sets,
/// nullability quirks (Setting.IsPrivate, Job.MaximumDuration, SiteTask.IsCompleted, Notification.IsRead —
/// all NULLABLE behind non-nullable CLR properties) and deliberate omissions (no audit columns on
/// Log/JobLog/Visitor/UrlMapping/Notification/SearchContent*) were established there by deriving each table
/// from its EntityBuilder plus every migration that altered it and CROSS-CHECKING against a live migrated
/// PostgreSQL database — this file inherits that work and translates names and types only. Names go through
/// the SAME rewrite the real migrations use: the context is configured exactly as
/// <c>PostgreSQLDatabase.UseDatabase</c> configures the live one (<c>UseNpgsql</c> +
/// <c>UseSnakeCaseNamingConvention</c>, minus the migrations-only history repository), with
/// <c>ConfigureFrameworkStore(new PostgreSQLDatabase())</c> supplying the snake_case rewriter for every
/// explicitly-named column. Types follow the live Npgsql mapping: <c>integer</c> identity
/// (<c>GENERATED ALWAYS</c>, matching <c>AddAutoIncrementColumn</c>'s <c>IdentityAlwaysColumn</c>),
/// <c>text</c>, <c>boolean</c>, <c>timestamp with time zone</c>. Never <c>EnsureCreated</c>, for the SQLite
/// fixture's reason: a schema built from the model under test could only ever agree with itself.
/// </remarks>
public sealed class PostgresFrameworkStoreConformanceTests : FrameworkStoreConformanceTests
{
    internal const string DatabasePrefix = "stomme_conformance_fs";

    private readonly string _tenantDatabase = DatabasePrefix + "t_" + Guid.NewGuid().ToString("N");
    private readonly string _masterDatabase = DatabasePrefix + "m_" + Guid.NewGuid().ToString("N");
    private readonly IRelationalDatabase _database = new PostgreSQLDatabase();
    private IFrameworkStoreSessionFactory _frameworkSessions = default!;
    private IMasterStoreSessionFactory _masterSessions = default!;

    protected override IFrameworkStoreSessionFactory FrameworkSessions => _frameworkSessions;

    protected override IMasterStoreSessionFactory MasterSessions => _masterSessions;

    /// <summary>True — as on every relational provider: <c>==</c> is SQL equality under the column's collation, and PostgreSQL's default collation is case-SENSITIVE. Verified by running this suite against the PostgreSQL test server.</summary>
    protected override bool ProviderStringEqualityIsOrdinal => true;

    /// <summary>
    /// TRUE — and this single override is the reason this fixture exists. EF translates
    /// <c>StartsWith</c> to <c>LIKE</c>, and <b>PostgreSQL's <c>LIKE</c> is case-SENSITIVE</b> where SQLite's
    /// and RavenDB's prefix matches are case-INSENSITIVE — the three-way split measured earlier. Both other
    /// fixtures answer <c>false</c> here; the live product's answer is <c>true</c>, and with this fixture the
    /// suite finally REPRESENTS it (<c>A_prefix_filter_is_expressible_on_every_provider</c> runs the
    /// <c>StartsWith("RUN")</c> probe against a live PostgreSQL and asserts zero rows). Verified by running,
    /// not by quoting the docs.
    /// </summary>
    protected override bool ProviderPrefixFilterIsOrdinal => true;

    public override async Task InitializeAsync()
    {
        await PostgresConformanceEndpoint.SweepStaleAsync(DatabasePrefix);

        await Task.WhenAll(
            CreateAsync(_tenantDatabase, TenantSchemaDdl),
            CreateAsync(_masterDatabase, MasterSchemaDdl));

        _frameworkSessions = new TestFrameworkStoreSessionFactory(NewTenantContext);
        _masterSessions = new TestMasterStoreSessionFactory(NewMasterContext);
    }

    public override async Task DisposeAsync() =>
        await Task.WhenAll(
            PostgresConformanceEndpoint.DropDatabaseAsync(_tenantDatabase),
            PostgresConformanceEndpoint.DropDatabaseAsync(_masterDatabase));

    private static async Task CreateAsync(string database, string ddl)
    {
        await PostgresConformanceEndpoint.CreateDatabaseAsync(database);
        await using var connection = new NpgsqlConnection(PostgresConformanceEndpoint.ConnectionString(database));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = ddl;
        await command.ExecuteNonQueryAsync();
    }

    protected override ISiteStore NewSites() => new TestSiteStore(NewTenantContext());

    protected override IPageStore NewPages() => new TestPageStore(NewTenantContext());

    protected override IModuleStore NewModules() => new TestModuleStore(NewTenantContext());

    protected override IUserAccountStore NewUsers() => new TestUserAccountStore(NewTenantContext());

    protected override ISecurityRoleStore NewRoles() => new TestSecurityRoleStore(NewTenantContext());

    protected override IUserRoleAssignmentStore NewUserRoles() => new TestUserRoleAssignmentStore(NewTenantContext());

    protected override ISettingStore NewSettings() => new TestSettingStore(NewTenantContext());

    // Bound to the master database — Setting is the FIRST aggregate the master database holds.
    protected override IMasterSettingStore NewMasterSettings() => new TestMasterSettingStore(NewMasterContext());

    // The Job/JobLog pair, also master-side: no tenant twin.
    protected override IJobStore NewJobs() => new TestJobStore(NewMasterContext());

    protected override IJobLogStore NewJobLogs() => new TestJobLogStore(NewMasterContext());

    // Theme, ModuleDefinition and the Tenant/Alias routing pair: master-side only.
    protected override IThemeStore NewThemes() => new TestThemeStore(NewMasterContext());

    protected override IModuleDefinitionStore NewModuleDefinitions() => new TestModuleDefinitionStore(NewMasterContext());

    protected override ITenantStore NewTenants() => new TestTenantStore(NewMasterContext());

    protected override IAliasStore NewAliases() => new TestAliasStore(NewMasterContext());

    // The Permission cluster: tenant-side only, no master half.
    protected override IPermissionStore NewPermissions() => new TestPermissionStore(NewTenantContext());

    // The five leaf clusters and the SiteGroup pair: tenant-side only.
    protected override ISiteGroupStore NewSiteGroups() => new TestSiteGroupStore(NewTenantContext());

    protected override ISiteGroupMemberStore NewSiteGroupMembers() => new TestSiteGroupMemberStore(NewTenantContext());

    protected override ILanguageStore NewLanguages() => new TestLanguageStore(NewTenantContext());

    protected override ILogStore NewLogs() => new TestLogStore(NewTenantContext());

    protected override IProfileStore NewProfiles() => new TestProfileStore(NewTenantContext());

    protected override ISiteTaskStore NewSiteTasks() => new TestSiteTaskStore(NewTenantContext());

    protected override INotificationStore NewNotifications() => new TestNotificationStore(NewTenantContext());

    // Visitor, UrlMapping and PageModule: tenant-side only.
    protected override IVisitorStore NewVisitors() => new TestVisitorStore(NewTenantContext());

    protected override IUrlMappingStore NewUrlMappings() => new TestUrlMappingStore(NewTenantContext());

    protected override IPageModuleStore NewPageModules() => new TestPageModuleStore(NewTenantContext());

    // Folder/File: tenant-side only.
    protected override IFolderStore NewFolders() => new TestFolderStore(NewTenantContext());

    protected override IFileStore NewFiles() => new TestFileStore(NewTenantContext());

    // The SearchContent cluster.
    protected override ISearchContentStore NewSearchContents() => new TestSearchContentStore(NewTenantContext());

    protected override ISearchContentPropertyStore NewSearchContentProperties() => new TestSearchContentPropertyStore(NewTenantContext());

    protected override ISearchContentWordStore NewSearchContentWords() => new TestSearchContentWordStore(NewTenantContext());

    protected override ISearchWordStore NewSearchWords() => new TestSearchWordStore(NewTenantContext());

    /// <summary>
    /// The EF search read — the REAL <c>EfSearchQueryStore</c>, for the SQLite fixture's reason: the
    /// harvested <c>GROUP BY</c>+<c>SUM</c> lives inside it, and this is its first run against the SQL
    /// dialect the product actually issues it on.
    /// </summary>
    protected override ISearchQueryStore NewSearchQueries() => new EfSearchQueryStore(FrameworkSessions);

    private DbContext NewTenantContext()
    {
        var builder = new DbContextOptionsBuilder();
        // The live provider's configuration (PostgreSQLDatabase.UseDatabase) minus the migrations-only
        // history repository: UseNpgsql + snake_case naming, so convention-named columns land on the same
        // names the real migrations created.
        builder.UseNpgsql(PostgresConformanceEndpoint.ConnectionString(_tenantDatabase))
            .UseSnakeCaseNamingConvention();
        return new TenantStoreContext(builder.Options, _database);
    }

    private DbContext NewMasterContext()
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseNpgsql(PostgresConformanceEndpoint.ConnectionString(_masterDatabase))
            .UseSnakeCaseNamingConvention();
        return new MasterStoreContext(builder.Options, _database);
    }

    /// <summary>The tenant half: the whole framework store, exactly as TenantDBContext maps it.</summary>
    private sealed class TenantStoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
    }

    /// <summary>The master half: exactly as MasterDBContext maps it.</summary>
    private sealed class MasterStoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
    }

    /// <summary>
    /// The shared body of every EF framework store under test — the same trivial-delegation composition the
    /// SQLite fixture uses (and <c>EfSiteStore</c> performs); see that fixture's remarks for why the real
    /// store classes are not instantiated here.
    /// </summary>
    private abstract class TestStore<TAggregate>(DbContext context) : IRepository<TAggregate, int>
        where TAggregate : AggregateRoot<int>
    {
        private readonly IRepository<TAggregate, int> _repository =
            new EfRepository<TAggregate, int>(context, new AggregateTracker(), new StubTenantContext(), []);

        public Task<TAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default) => _repository.GetAllAsync(ct);

        public Task AddAsync(TAggregate aggregate, CancellationToken ct = default) => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default) => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default) => _repository.DeleteAsync(aggregate, ct);
    }

    private sealed class TestSiteStore(DbContext context) : TestStore<SiteAggregate>(context), ISiteStore;

    private sealed class TestPageStore(DbContext context) : TestStore<PageAggregate>(context), IPageStore;

    private sealed class TestModuleStore(DbContext context) : TestStore<ModuleAggregate>(context), IModuleStore;

    private sealed class TestUserAccountStore(DbContext context) : TestStore<UserAggregate>(context), IUserAccountStore;

    private sealed class TestSecurityRoleStore(DbContext context) : TestStore<RoleAggregate>(context), ISecurityRoleStore;

    private sealed class TestUserRoleAssignmentStore(DbContext context) : TestStore<UserRoleAggregate>(context), IUserRoleAssignmentStore;

    private sealed class TestSettingStore(DbContext context) : TestStore<SettingAggregate>(context), ISettingStore;

    private sealed class TestMasterSettingStore(DbContext context) : TestStore<SettingAggregate>(context), IMasterSettingStore;

    private sealed class TestJobStore(DbContext context) : TestStore<JobAggregate>(context), IJobStore;

    private sealed class TestJobLogStore(DbContext context) : TestStore<JobLogAggregate>(context), IJobLogStore;

    private sealed class TestThemeStore(DbContext context) : TestStore<ThemeAggregate>(context), IThemeStore;

    private sealed class TestModuleDefinitionStore(DbContext context) : TestStore<ModuleDefinitionAggregate>(context), IModuleDefinitionStore;

    private sealed class TestTenantStore(DbContext context) : TestStore<TenantAggregate>(context), ITenantStore;

    private sealed class TestAliasStore(DbContext context) : TestStore<AliasAggregate>(context), IAliasStore;

    private sealed class TestPermissionStore(DbContext context) : TestStore<PermissionAggregate>(context), IPermissionStore;

    private sealed class TestSiteGroupStore(DbContext context) : TestStore<SiteGroupAggregate>(context), ISiteGroupStore;

    private sealed class TestSiteGroupMemberStore(DbContext context) : TestStore<SiteGroupMemberAggregate>(context), ISiteGroupMemberStore;

    private sealed class TestLanguageStore(DbContext context) : TestStore<LanguageAggregate>(context), ILanguageStore;

    private sealed class TestLogStore(DbContext context) : TestStore<LogAggregate>(context), ILogStore;

    private sealed class TestProfileStore(DbContext context) : TestStore<ProfileAggregate>(context), IProfileStore;

    private sealed class TestSiteTaskStore(DbContext context) : TestStore<SiteTaskAggregate>(context), ISiteTaskStore;

    private sealed class TestNotificationStore(DbContext context) : TestStore<NotificationAggregate>(context), INotificationStore;

    private sealed class TestVisitorStore(DbContext context) : TestStore<VisitorAggregate>(context), IVisitorStore;

    private sealed class TestUrlMappingStore(DbContext context) : TestStore<UrlMappingAggregate>(context), IUrlMappingStore;

    private sealed class TestPageModuleStore(DbContext context) : TestStore<PageModuleAggregate>(context), IPageModuleStore;

    private sealed class TestFolderStore(DbContext context) : TestStore<FolderAggregate>(context), IFolderStore;

    private sealed class TestFileStore(DbContext context) : TestStore<FileAggregate>(context), IFileStore;

    private sealed class TestSearchContentStore(DbContext context) : TestStore<SearchContentAggregate>(context), ISearchContentStore;

    private sealed class TestSearchContentPropertyStore(DbContext context) : TestStore<SearchContentPropertyAggregate>(context), ISearchContentPropertyStore;

    private sealed class TestSearchContentWordStore(DbContext context) : TestStore<SearchContentWordAggregate>(context), ISearchContentWordStore;

    private sealed class TestSearchWordStore(DbContext context) : TestStore<SearchWordAggregate>(context), ISearchWordStore;

    private sealed class TestFrameworkStoreSessionFactory(Func<DbContext> context) : IFrameworkStoreSessionFactory
    {
        private readonly EfDocumentSessionFactory _factory = new(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
        {
            [StoreNames.Main] = context
        });

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }

    private sealed class TestMasterStoreSessionFactory(Func<DbContext> context) : IMasterStoreSessionFactory
    {
        private readonly EfDocumentSessionFactory _factory = new(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
        {
            [StoreNames.Main] = context
        });

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }

    private sealed class StubTenantContext : ITenantContext
    {
        public TenantId? CurrentTenantId => TenantId.Default;

        public bool IsSystemScope => false;
    }

    // ------------------------------------------------------------------------------------------------
    // The DDL below is the SQLite fixture's harvested schema, translated to PostgreSQL: identifiers
    // snake_cased exactly as EFCore.NamingConventions/PostgreSQLDatabase.RewriteName rewrite them, types
    // per the live Npgsql mapping (integer / text / boolean / timestamp with time zone), identity columns
    // GENERATED ALWAYS (AddAutoIncrementColumn uses IdentityAlwaysColumn). Column sets and nullability are
    // reproduced 1:1, including every deliberate quirk the SQLite fixture documents — see its comments for
    // the provenance of each table; they are not repeated here.
    // ------------------------------------------------------------------------------------------------

    private const string SettingTableDdl = """
        CREATE TABLE "setting" (
            "setting_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_setting" PRIMARY KEY,
            "entity_name" text NOT NULL,
            "entity_id" integer NOT NULL,
            "setting_name" text NOT NULL,
            "setting_value" text NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "is_private" boolean NULL
        );
        """;

    private const string MasterSchemaDdl = SettingTableDdl + """

        CREATE TABLE "job" (
            "job_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_job" PRIMARY KEY,
            "name" text NOT NULL,
            "job_type" text NOT NULL,
            "frequency" text NOT NULL,
            "interval" integer NOT NULL,
            "start_date" timestamp with time zone NULL,
            "end_date" timestamp with time zone NULL,
            "is_enabled" boolean NOT NULL,
            "is_started" boolean NOT NULL,
            "is_executing" boolean NOT NULL,
            "next_execution" timestamp with time zone NULL,
            "retention_history" integer NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "maximum_duration" integer NULL
        );
        CREATE TABLE "job_log" (
            "job_log_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_job_log" PRIMARY KEY,
            "job_id" integer NOT NULL,
            "start_date" timestamp with time zone NOT NULL,
            "finish_date" timestamp with time zone NULL,
            "succeeded" boolean NULL,
            "notes" text NULL,
            "server" text NULL,
            "instance" text NULL
        );
        CREATE TABLE "theme" (
            "theme_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_theme" PRIMARY KEY,
            "theme_name" text NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "name" text NULL,
            "version" text NULL
        );
        CREATE TABLE "module_definition" (
            "module_definition_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_module_definition" PRIMARY KEY,
            "module_definition_name" text NOT NULL,
            "name" text NULL,
            "description" text NULL,
            "categories" text NULL,
            "version" text NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        CREATE TABLE "tenant" (
            "tenant_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_tenant" PRIMARY KEY,
            "name" text NOT NULL,
            "db_connection_string" text NOT NULL,
            "version" text NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "db_type" text NULL
        );
        CREATE TABLE "alias" (
            "alias_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_alias" PRIMARY KEY,
            "name" text NOT NULL,
            "tenant_id" integer NOT NULL,
            "site_id" integer NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "is_default" boolean NULL
        );
        """;

    private const string TenantSchemaDdl = SettingTableDdl + """

        CREATE TABLE "site" (
            "site_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_site" PRIMARY KEY,
            "name" text NOT NULL,
            "logo_file_id" integer NULL,
            "favicon_file_id" integer NULL,
            "default_theme_type" text NOT NULL,
            "default_container_type" text NOT NULL,
            "pwa_is_enabled" boolean NOT NULL,
            "pwa_app_icon_file_id" integer NULL,
            "pwa_splash_icon_file_id" integer NULL,
            "allow_registration" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "deleted_by" text NULL,
            "deleted_on" timestamp with time zone NULL,
            "is_deleted" boolean NOT NULL,
            "admin_container_type" text NULL,
            "admin_theme_type" text NULL,
            "site_guid" text NULL,
            "runtime" text NULL,
            "render_mode" text NULL,
            "visitor_tracking" boolean NULL,
            "capture_broken_urls" boolean NULL,
            "version" text NULL,
            "head_content" text NULL,
            "body_content" text NULL,
            "prerender" boolean NULL,
            "hybrid" boolean NULL,
            "time_zone_id" text NULL,
            "enhanced_navigation" boolean NULL,
            "culture_code" text NULL
        );
        CREATE TABLE "page" (
            "page_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_page" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "path" text NOT NULL,
            "name" text NOT NULL,
            "title" text NULL,
            "theme_type" text NULL,
            "icon" text NOT NULL,
            "parent_id" integer NULL,
            "order" integer NOT NULL,
            "is_navigation" boolean NOT NULL,
            "url" text NULL,
            "user_id" integer NULL,
            "is_personalizable" boolean NOT NULL,
            "default_container_type" text NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "deleted_by" text NULL,
            "deleted_on" timestamp with time zone NULL,
            "is_deleted" boolean NOT NULL,
            "is_clickable" boolean NULL,
            "head_content" text NULL,
            "body_content" text NULL,
            "effective_date" timestamp with time zone NULL,
            "expiry_date" timestamp with time zone NULL,
            "path_normalized" text NULL
        );
        CREATE TABLE "module" (
            "module_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_module" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "module_definition_name" text NOT NULL,
            "all_pages" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        CREATE TABLE "user" (
            "user_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_user" PRIMARY KEY,
            "username" text NOT NULL,
            "display_name" text NOT NULL,
            "email" text NOT NULL,
            "photo_file_id" integer NULL,
            "last_login_on" timestamp with time zone NULL,
            "last_ip_address" text NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "deleted_by" text NULL,
            "deleted_on" timestamp with time zone NULL,
            "is_deleted" boolean NOT NULL,
            "two_factor_required" boolean NOT NULL,
            "two_factor_code" text NULL,
            "two_factor_expiry" timestamp with time zone NULL,
            "time_zone_id" text NULL,
            "culture_code" text NULL
        );
        CREATE TABLE "role" (
            "role_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_role" PRIMARY KEY,
            "site_id" integer NULL,
            "name" text NOT NULL,
            "description" text NOT NULL,
            "is_auto_assigned" boolean NOT NULL,
            "is_system" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        CREATE TABLE "user_role" (
            "user_role_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_user_role" PRIMARY KEY,
            "user_id" integer NOT NULL,
            "role_id" integer NOT NULL,
            "effective_date" timestamp with time zone NULL,
            "expiry_date" timestamp with time zone NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        CREATE TABLE "permission" (
            "permission_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_permission" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "entity_name" text NOT NULL,
            "entity_id" integer NOT NULL,
            "permission_name" text NOT NULL,
            "role_id" integer NULL,
            "user_id" integer NULL,
            "is_authorized" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        """ + LeafClusterDdl;

    private const string LeafClusterDdl = """

        CREATE TABLE "language" (
            "language_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_language" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "code" text NOT NULL,
            "is_default" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        CREATE TABLE "log" (
            "log_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_log" PRIMARY KEY,
            "site_id" integer NULL,
            "log_date" timestamp with time zone NOT NULL,
            "page_id" integer NULL,
            "module_id" integer NULL,
            "user_id" integer NULL,
            "url" text NOT NULL,
            "server" text NOT NULL,
            "category" text NOT NULL,
            "feature" text NOT NULL,
            "function" text NOT NULL,
            "level" text NOT NULL,
            "message" text NOT NULL,
            "message_template" text NOT NULL,
            "exception" text NULL,
            "properties" text NULL,
            "instance" text NULL,
            "remote_ip_address" text NULL
        );
        CREATE TABLE "profile" (
            "profile_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_profile" PRIMARY KEY,
            "site_id" integer NULL,
            "name" text NOT NULL,
            "title" text NOT NULL,
            "description" text NULL,
            "category" text NOT NULL,
            "view_order" integer NOT NULL,
            "max_length" integer NOT NULL,
            "default_value" text NULL,
            "is_required" boolean NOT NULL,
            "is_private" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "options" text NULL,
            "validation" text NULL,
            "rows" integer NULL,
            "autocomplete" text NULL
        );
        CREATE TABLE "site_task" (
            "site_task_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_site_task" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "name" text NOT NULL,
            "type" text NOT NULL,
            "parameters" text NULL,
            "is_completed" boolean NULL,
            "status" text NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );
        CREATE TABLE "notification" (
            "notification_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_notification" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "from_user_id" integer NULL,
            "to_user_id" integer NULL,
            "to_email" text NOT NULL,
            "parent_id" integer NULL,
            "subject" text NOT NULL,
            "body" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "is_delivered" boolean NOT NULL,
            "delivered_on" timestamp with time zone NULL,
            "deleted_by" text NULL,
            "deleted_on" timestamp with time zone NULL,
            "is_deleted" boolean NOT NULL,
            "from_display_name" text NULL,
            "from_email" text NULL,
            "to_display_name" text NULL,
            "send_on" timestamp with time zone NULL,
            "is_read" boolean NULL
        );

        CREATE TABLE "site_group" (
            "site_group_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_site_group" PRIMARY KEY,
            "name" text NOT NULL,
            "type" text NOT NULL,
            "primary_site_id" integer NOT NULL,
            "synchronize" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );

        CREATE TABLE "site_group_member" (
            "site_group_member_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_site_group_member" PRIMARY KEY,
            "site_group_id" integer NOT NULL,
            "site_id" integer NOT NULL,
            "synchronized_on" timestamp with time zone NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );

        CREATE UNIQUE INDEX "ix_site_group_member" ON "site_group_member" ("site_id", "site_group_id");

        CREATE TABLE "visitor" (
            "visitor_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_visitor" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "user_id" integer NULL,
            "visits" integer NOT NULL,
            "ip_address" text NOT NULL,
            "user_agent" text NOT NULL,
            "language" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "visited_on" timestamp with time zone NOT NULL,
            "referrer" text NULL,
            "url" text NULL
        );

        CREATE TABLE "url_mapping" (
            "url_mapping_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_url_mapping" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "url" text NOT NULL,
            "mapped_url" text NOT NULL,
            "requests" integer NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "requested_on" timestamp with time zone NOT NULL,
            "referrer" text NULL,
            "url_normalized" text NULL
        );

        CREATE UNIQUE INDEX "ix_url_mapping" ON "url_mapping" ("site_id", "url");

        CREATE TABLE "folder" (
            "folder_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_folder" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "parent_id" integer NULL,
            "name" text NOT NULL,
            "path" text NOT NULL,
            "order" integer NOT NULL,
            "is_system" boolean NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "type" text NULL,
            "capacity" integer NULL,
            "image_sizes" text NULL,
            "cache_control" text NULL,
            "path_normalized" text NULL
        );

        CREATE UNIQUE INDEX "ix_folder" ON "folder" ("site_id", "path");

        CREATE TABLE "file" (
            "file_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_file" PRIMARY KEY,
            "folder_id" integer NOT NULL,
            "name" text NOT NULL,
            "extension" text NOT NULL,
            "size" integer NOT NULL,
            "image_height" integer NOT NULL,
            "image_width" integer NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "description" text NULL,
            "name_normalized" text NULL
        );

        CREATE UNIQUE INDEX "ix_file" ON "file" ("folder_id", "name");

        CREATE TABLE "search_content" (
            "search_content_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_search_content" PRIMARY KEY,
            "site_id" integer NOT NULL,
            "entity_name" text NOT NULL,
            "entity_id" text NOT NULL,
            "title" text NOT NULL,
            "description" text NOT NULL,
            "body" text NOT NULL,
            "url" text NOT NULL,
            "permissions" text NOT NULL,
            "content_modified_by" text NOT NULL,
            "content_modified_on" timestamp with time zone NOT NULL,
            "additional_content" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL
        );

        CREATE UNIQUE INDEX "ix_search_content" ON "search_content" ("site_id", "entity_name", "entity_id");

        CREATE TABLE "search_content_property" (
            "property_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_search_content_property" PRIMARY KEY,
            "search_content_id" integer NOT NULL,
            "name" text NOT NULL,
            "value" text NOT NULL
        );

        CREATE UNIQUE INDEX "ix_search_content_property" ON "search_content_property" ("search_content_id", "name");

        CREATE TABLE "search_content_word" (
            "search_content_word_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_search_content_word" PRIMARY KEY,
            "search_content_id" integer NOT NULL,
            "search_word_id" integer NOT NULL,
            "count" integer NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_on" timestamp with time zone NOT NULL
        );

        CREATE UNIQUE INDEX "ix_search_content_word" ON "search_content_word" ("search_content_id", "search_word_id");

        CREATE TABLE "search_word" (
            "search_word_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_search_word" PRIMARY KEY,
            "word" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL
        );

        CREATE UNIQUE INDEX "ix_search_word" ON "search_word" ("word");

        CREATE TABLE "page_module" (
            "page_module_id" integer GENERATED ALWAYS AS IDENTITY CONSTRAINT "pk_page_module" PRIMARY KEY,
            "page_id" integer NOT NULL,
            "module_id" integer NOT NULL,
            "title" text NOT NULL,
            "pane" text NOT NULL,
            "order" integer NOT NULL,
            "container_type" text NOT NULL,
            "created_by" text NOT NULL,
            "created_on" timestamp with time zone NOT NULL,
            "modified_by" text NOT NULL,
            "modified_on" timestamp with time zone NOT NULL,
            "deleted_by" text NULL,
            "deleted_on" timestamp with time zone NULL,
            "is_deleted" boolean NOT NULL,
            "effective_date" timestamp with time zone NULL,
            "expiry_date" timestamp with time zone NULL,
            "header" text NULL,
            "footer" text NULL
        );
        """;
}
