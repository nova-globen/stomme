using Stomme.Database.Sqlite;
using Stomme.Databases.Interfaces;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Repository.Stores;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.Conformance.Tests;

/// <summary>
/// The framework-store conformance suite against the EF Core provider — the reference implementation, and the
/// side that must not regress.
/// <para>
/// <b>Two SQLite databases, mirroring the tenant and master contexts.</b> The tenant connection carries the whole
/// framework store; the master connection carries Setting alone, because that is the ONE aggregate the master
/// database legitimately holds (<c>ConfigureMasterFrameworkStore</c>). Two connections, not two tables — the
/// stores differ by DATABASE, which is the property the Setting cluster's separation test checks.
/// </para>
/// </summary>
/// <remarks>
/// <b>What this fixture builds, and the one honest gap.</b> The stores here are
/// <see cref="EfRepository{TAggregate, TId}"/> over a context configured by the REAL
/// <c>ConfigureFrameworkStore</c> mapping, against a schema written BY HAND to match the tenant migration set
/// — never <c>EnsureCreated</c>, which would build the schema from the very model under test and could only
/// ever agree with itself. That is the composition <c>EfSiteStore</c> and friends perform
/// (<c>new EfRepository&lt;T, int&gt;(context, tracker, tenantContext, serializers)</c>), with one difference:
/// the context is a test-local <see cref="DbContext"/> rather than a <c>TenantDBContext</c>, because
/// <c>TenantDBContext</c> resolves its connection through <c>ITenantManager</c>/<c>IConfigurationRoot</c> at
/// <c>OnConfiguring</c> time and would drag the whole host into a storage test. So the EF store CLASSES
/// (trivial delegation shims) are not themselves instantiated here, while the RavenDB side resolves its real
/// stores from the container. This is the established pattern for the cluster tests in Stomme.Tests
/// (SettingStoreBehaviourTests and its four siblings); src/Stomme.Persistence.RavenDb/README.md states the asymmetry rather than
/// letting it be inferred.
/// </remarks>
public sealed class EfCoreFrameworkStoreConformanceTests : FrameworkStoreConformanceTests
{
    private readonly IRelationalDatabase _database = new SqliteDatabase();
    private SqliteConnection _tenantConnection = default!;
    private SqliteConnection _masterConnection = default!;
    private IFrameworkStoreSessionFactory _frameworkSessions = default!;
    private IMasterStoreSessionFactory _masterSessions = default!;

    protected override IFrameworkStoreSessionFactory FrameworkSessions => _frameworkSessions;

    protected override IMasterStoreSessionFactory MasterSessions => _masterSessions;

    /// <summary>True — EF Core translates <c>==</c> to SQL equality under the column's collation, which is case-SENSITIVE on every provider Stomme supports.</summary>
    protected override bool ProviderStringEqualityIsOrdinal => true;

    /// <summary>
    /// FALSE — and it is the fixture's answer, not EF Core's. EF translates <c>StartsWith</c> to <c>LIKE</c>,
    /// and <b>SQLite's <c>LIKE</c> is case-INSENSITIVE for ASCII</b>. PostgreSQL's is case-SENSITIVE, so the
    /// live product would answer <c>true</c> here — measured directly on the PostgreSQL test server
    /// (<c>SELECT 'running' LIKE 'RUN%'</c> returns <c>f</c>). This fixture is SQLite, so that case is
    /// not represented in this suite at all.
    /// </summary>
    protected override bool ProviderPrefixFilterIsOrdinal => false;

    public override async Task InitializeAsync()
    {
        _tenantConnection = new SqliteConnection("Data Source=:memory:");
        await _tenantConnection.OpenAsync();
        await ExecuteAsync(_tenantConnection, TenantSchemaDdl);

        _masterConnection = new SqliteConnection("Data Source=:memory:");
        await _masterConnection.OpenAsync();
        await ExecuteAsync(_masterConnection, MasterSchemaDdl);

        _frameworkSessions = new TestFrameworkStoreSessionFactory(NewTenantContext);
        _masterSessions = new TestMasterStoreSessionFactory(NewMasterContext);
    }

    public override async Task DisposeAsync()
    {
        await _tenantConnection.DisposeAsync();
        await _masterConnection.DisposeAsync();
    }

    protected override ISiteStore NewSites() => new TestSiteStore(NewTenantContext());

    protected override IPageStore NewPages() => new TestPageStore(NewTenantContext());

    protected override IModuleStore NewModules() => new TestModuleStore(NewTenantContext());

    protected override IUserAccountStore NewUsers() => new TestUserAccountStore(NewTenantContext());

    protected override ISecurityRoleStore NewRoles() => new TestSecurityRoleStore(NewTenantContext());

    protected override IUserRoleAssignmentStore NewUserRoles() => new TestUserRoleAssignmentStore(NewTenantContext());

    protected override ISettingStore NewSettings() => new TestSettingStore(NewTenantContext());

    // Bound to the master connection — Setting is the FIRST aggregate the master database holds.
    protected override IMasterSettingStore NewMasterSettings() => new TestMasterSettingStore(NewMasterContext());

    // The Job/JobLog pair, also bound to the master connection: master-side only, no tenant twin.
    protected override IJobStore NewJobs() => new TestJobStore(NewMasterContext());

    protected override IJobLogStore NewJobLogs() => new TestJobLogStore(NewMasterContext());

    // The Theme cluster, also bound to the master connection: master-side only, no tenant twin.
    protected override IThemeStore NewThemes() => new TestThemeStore(NewMasterContext());

    // The ModuleDefinition cluster, also bound to the master connection: master-side only, no tenant twin.
    protected override IModuleDefinitionStore NewModuleDefinitions() => new TestModuleDefinitionStore(NewMasterContext());

    // The Tenant/Alias routing pair — master-side, over the master connection like Theme/ModuleDefinition.
    protected override ITenantStore NewTenants() => new TestTenantStore(NewMasterContext());

    protected override IAliasStore NewAliases() => new TestAliasStore(NewMasterContext());

    // The Permission cluster. Tenant connection only: MasterDBContext never mapped Permission and the
    // master migration set never created the table.
    protected override IPermissionStore NewPermissions() => new TestPermissionStore(NewTenantContext());

    // The five leaf clusters. Tenant connection only: none of them has a master half.
    // The SiteGroup pair. Tenant connection only: neither has a master half.
    protected override ISiteGroupStore NewSiteGroups() => new TestSiteGroupStore(NewTenantContext());

    protected override ISiteGroupMemberStore NewSiteGroupMembers() => new TestSiteGroupMemberStore(NewTenantContext());

    protected override ILanguageStore NewLanguages() => new TestLanguageStore(NewTenantContext());

    protected override ILogStore NewLogs() => new TestLogStore(NewTenantContext());

    protected override IProfileStore NewProfiles() => new TestProfileStore(NewTenantContext());

    protected override ISiteTaskStore NewSiteTasks() => new TestSiteTaskStore(NewTenantContext());

    protected override INotificationStore NewNotifications() => new TestNotificationStore(NewTenantContext());

    // Visitor, UrlMapping and PageModule. Tenant connection only: none of the three has a master half.
    protected override IVisitorStore NewVisitors() => new TestVisitorStore(NewTenantContext());

    protected override IUrlMappingStore NewUrlMappings() => new TestUrlMappingStore(NewTenantContext());

    protected override IPageModuleStore NewPageModules() => new TestPageModuleStore(NewTenantContext());

    // Folder. Tenant connection only: no master half. The File cluster is on the seam too: the
    // normalized-column pattern removed the blocker that once kept it off, and IFileStore exists (ISiteStore.cs)
    // and is exercised by NewFiles() immediately below.
    protected override IFolderStore NewFolders() => new TestFolderStore(NewTenantContext());

    protected override IFileStore NewFiles() => new TestFileStore(NewTenantContext());

    // The SearchContent cluster. Four CRUD stores over EfRepository, as above.
    protected override ISearchContentStore NewSearchContents() => new TestSearchContentStore(NewTenantContext());

    protected override ISearchContentPropertyStore NewSearchContentProperties() => new TestSearchContentPropertyStore(NewTenantContext());

    protected override ISearchContentWordStore NewSearchContentWords() => new TestSearchContentWordStore(NewTenantContext());

    protected override ISearchWordStore NewSearchWords() => new TestSearchWordStore(NewTenantContext());

    /// <summary>
    /// The EF search read — <b>the REAL <c>EfSearchQueryStore</c>, not a fixture copy</b>.
    /// </summary>
    /// <remarks>
    /// Every other store here is hand-built over <c>EfRepository</c>, because their behaviour IS
    /// EfRepository's and the real classes need tenant DI this fixture deliberately does not have. This one is
    /// different: the harvested <c>GROUP BY</c> + <c>SUM</c> lives inside <c>EfSearchQueryStore</c> and is
    /// exactly what the RavenDB facet half must agree with, so a reimplementation here would assert that two
    /// test copies match and prove nothing. It only needs a session factory, which this fixture has — see the
    /// InternalsVisibleTo note in Stomme.Server.csproj.
    /// </remarks>
    protected override ISearchQueryStore NewSearchQueries() => new EfSearchQueryStore(FrameworkSessions);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private DbContext NewTenantContext()
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseSqlite(_tenantConnection);
        return new TenantStoreContext(builder.Options, _database);
    }

    private DbContext NewMasterContext()
    {
        var builder = new DbContextOptionsBuilder();
        builder.UseSqlite(_masterConnection);
        return new MasterStoreContext(builder.Options, _database);
    }

    /// <summary>The tenant half: the whole framework store, exactly as TenantDBContext maps it.</summary>
    private sealed class TenantStoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
    }

    /// <summary>The master half: SettingAggregate and nothing else, exactly as MasterDBContext maps it.</summary>
    private sealed class MasterStoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);
    }

    /// <summary>
    /// The shared body of every EF framework store under test: one aggregate, one
    /// <see cref="EfRepository{TAggregate, TId}"/>, forwarded — the same composition
    /// <see cref="EfSiteStore"/> performs, and the mirror image of the RavenDB side's
    /// <c>RavenFrameworkStore&lt;T&gt;</c>. The stores below differ only in the interface they close.
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

    // The Job/JobLog pair, over the master connection.
    private sealed class TestJobStore(DbContext context) : TestStore<JobAggregate>(context), IJobStore;

    private sealed class TestJobLogStore(DbContext context) : TestStore<JobLogAggregate>(context), IJobLogStore;

    private sealed class TestThemeStore(DbContext context) : TestStore<ThemeAggregate>(context), IThemeStore;

    private sealed class TestModuleDefinitionStore(DbContext context) : TestStore<ModuleDefinitionAggregate>(context), IModuleDefinitionStore;

    // The Tenant/Alias routing pair, over the master connection.
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

    // The SearchContent cluster.
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

    // The tenant schema exactly as the migration set leaves it — lifted from the per-cluster behaviour tests
    // in Stomme.Tests, which derived each table from its EntityBuilder plus the migrations that altered it.
    // Note Setting.IsPrivate is NULLABLE against a non-nullable CLR property: harvested and deliberate, and
    // reproduced rather than tidied, because tidying it here would hide a real read failure.
    private const string SettingTableDdl = """
        CREATE TABLE "Setting" (
            "SettingId" INTEGER NOT NULL CONSTRAINT "PK_Setting" PRIMARY KEY AUTOINCREMENT,
            "EntityName" TEXT NOT NULL,
            "EntityId" INTEGER NOT NULL,
            "SettingName" TEXT NOT NULL,
            "SettingValue" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "IsPrivate" INTEGER NULL
        );
        """;

    // The MASTER schema: Setting, plus the Job/JobLog pair, Theme, ModuleDefinition, and the Tenant/Alias
    // routing pair that completed the set. These SEVEN are the only framework-store aggregates the master
    // database holds — everything else is a per-tenant table. tenant has EXACTLY 9 columns — tenant_id, name (NOT
    // NULL, UNIQUE), db_connection_string (NOT NULL), NULLABLE version + db_type (db_type is absent from
    // TenantEntityBuilder — migration 02010001 adds it — yet present in the live table; version is the string
    // AggregateRoot.Version shadows, carried by TenantAggregate.TenantVersion), and the audit quartet. alias has
    // EXACTLY 9 — alias_id, name (NOT NULL, UNIQUE), tenant_id + site_id (NOT NULL), NULLABLE is_default (absent from
    // AliasEntityBuilder — migration 03000201 adds it — yet present), and the audit quartet; alias has NO version
    // column. The Job/JobLog/Theme/ModuleDefinition tables are derived from their EntityBuilders plus
    // the migrations that altered them, then CROSS-CHECKED column-for-column against the live migrated PostgreSQL
    // database (stomme-pg-44357, host 45432): job has 17 columns incl. the audit quartet and a NULLABLE
    // maximum_duration (10020002 adds it late, behind a non-nullable CLR property — reproduced rather than
    // tidied); job_log has 8 and NONE of the audit quartet (JobLogEntityBuilder extends BaseEntityBuilder), with
    // Server/Instance added by 10020001; theme has EXACTLY 8 — theme_id, theme_name, the audit quartet (all NOT
    // NULL), and NULLABLE name/version (the `version` string being what AggregateRoot.Version shadows, carried by
    // ThemeAggregate.ThemeVersion); module_definition has EXACTLY 10 — module_definition_id, module_definition_name
    // (NOT NULL), NULLABLE name/description/categories/version, and the audit quartet (all NOT NULL).
    private const string MasterSchemaDdl = SettingTableDdl + """

        CREATE TABLE "Job" (
            "JobId" INTEGER NOT NULL CONSTRAINT "PK_Job" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "JobType" TEXT NOT NULL,
            "Frequency" TEXT NOT NULL,
            "Interval" INTEGER NOT NULL,
            "StartDate" TEXT NULL,
            "EndDate" TEXT NULL,
            "IsEnabled" INTEGER NOT NULL,
            "IsStarted" INTEGER NOT NULL,
            "IsExecuting" INTEGER NOT NULL,
            "NextExecution" TEXT NULL,
            "RetentionHistory" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "MaximumDuration" INTEGER NULL
        );
        CREATE TABLE "JobLog" (
            "JobLogId" INTEGER NOT NULL CONSTRAINT "PK_JobLog" PRIMARY KEY AUTOINCREMENT,
            "JobId" INTEGER NOT NULL,
            "StartDate" TEXT NOT NULL,
            "FinishDate" TEXT NULL,
            "Succeeded" INTEGER NULL,
            "Notes" TEXT NULL,
            "Server" TEXT NULL,
            "Instance" TEXT NULL
        );
        CREATE TABLE "Theme" (
            "ThemeId" INTEGER NOT NULL CONSTRAINT "PK_Theme" PRIMARY KEY AUTOINCREMENT,
            "ThemeName" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "Name" TEXT NULL,
            "Version" TEXT NULL
        );
        CREATE TABLE "ModuleDefinition" (
            "ModuleDefinitionId" INTEGER NOT NULL CONSTRAINT "PK_ModuleDefinition" PRIMARY KEY AUTOINCREMENT,
            "ModuleDefinitionName" TEXT NOT NULL,
            "Name" TEXT NULL,
            "Description" TEXT NULL,
            "Categories" TEXT NULL,
            "Version" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        CREATE TABLE "Tenant" (
            "TenantId" INTEGER NOT NULL CONSTRAINT "PK_Tenant" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "DBConnectionString" TEXT NOT NULL,
            "Version" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "DBType" TEXT NULL
        );
        CREATE TABLE "Alias" (
            "AliasId" INTEGER NOT NULL CONSTRAINT "PK_Alias" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "TenantId" INTEGER NOT NULL,
            "SiteId" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "IsDefault" INTEGER NULL
        );
        """;

    private const string TenantSchemaDdl = SettingTableDdl + """

        CREATE TABLE "Site" (
            "SiteId" INTEGER NOT NULL CONSTRAINT "PK_Site" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "LogoFileId" INTEGER NULL,
            "FaviconFileId" INTEGER NULL,
            "DefaultThemeType" TEXT NOT NULL,
            "DefaultContainerType" TEXT NOT NULL,
            "PwaIsEnabled" INTEGER NOT NULL,
            "PwaAppIconFileId" INTEGER NULL,
            "PwaSplashIconFileId" INTEGER NULL,
            "AllowRegistration" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "DeletedBy" TEXT NULL,
            "DeletedOn" TEXT NULL,
            "IsDeleted" INTEGER NOT NULL,
            "AdminContainerType" TEXT NULL,
            "AdminThemeType" TEXT NULL,
            "SiteGuid" TEXT NULL,
            "Runtime" TEXT NULL,
            "RenderMode" TEXT NULL,
            "VisitorTracking" INTEGER NULL,
            "CaptureBrokenUrls" INTEGER NULL,
            "Version" TEXT NULL,
            "HeadContent" TEXT NULL,
            "BodyContent" TEXT NULL,
            "Prerender" INTEGER NULL,
            "Hybrid" INTEGER NULL,
            "TimeZoneId" TEXT NULL,
            "EnhancedNavigation" INTEGER NULL,
            "CultureCode" TEXT NULL
        );
        CREATE TABLE "Page" (
            "PageId" INTEGER NOT NULL CONSTRAINT "PK_Page" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "Path" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "Title" TEXT NULL,
            "ThemeType" TEXT NULL,
            "Icon" TEXT NOT NULL,
            "ParentId" INTEGER NULL,
            "Order" INTEGER NOT NULL,
            "IsNavigation" INTEGER NOT NULL,
            "Url" TEXT NULL,
            "UserId" INTEGER NULL,
            "IsPersonalizable" INTEGER NOT NULL,
            "DefaultContainerType" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "DeletedBy" TEXT NULL,
            "DeletedOn" TEXT NULL,
            "IsDeleted" INTEGER NOT NULL,
            "IsClickable" INTEGER NULL,
            "HeadContent" TEXT NULL,
            "BodyContent" TEXT NULL,
            "EffectiveDate" TEXT NULL,
            "ExpiryDate" TEXT NULL,
            "PathNormalized" TEXT NULL
        );

        -- STILL uniquely (SiteId, Path) case-SENSITIVE conceptually; PathNormalized was added as a LOOKUP
        -- column so PageRepository.GetPageAsync(path, siteId) agrees across providers (case-INSENSITIVE),
        -- matching SiteRouter's own OrdinalIgnoreCase primary match and the Folder/File/UrlMapping precedent.
        CREATE TABLE "Module" (
            "ModuleId" INTEGER NOT NULL CONSTRAINT "PK_Module" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "ModuleDefinitionName" TEXT NOT NULL,
            "AllPages" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        CREATE TABLE "User" (
            "UserId" INTEGER NOT NULL CONSTRAINT "PK_User" PRIMARY KEY AUTOINCREMENT,
            "Username" TEXT NOT NULL,
            "DisplayName" TEXT NOT NULL,
            "Email" TEXT NOT NULL,
            "PhotoFileId" INTEGER NULL,
            "LastLoginOn" TEXT NULL,
            "LastIpAddress" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "DeletedBy" TEXT NULL,
            "DeletedOn" TEXT NULL,
            "IsDeleted" INTEGER NOT NULL,
            "TwoFactorRequired" INTEGER NOT NULL,
            "TwoFactorCode" TEXT NULL,
            "TwoFactorExpiry" TEXT NULL,
            "TimeZoneId" TEXT NULL,
            "CultureCode" TEXT NULL
        );
        CREATE TABLE "Role" (
            "RoleId" INTEGER NOT NULL CONSTRAINT "PK_Role" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NULL,
            "Name" TEXT NOT NULL,
            "Description" TEXT NOT NULL,
            "IsAutoAssigned" INTEGER NOT NULL,
            "IsSystem" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        CREATE TABLE "UserRole" (
            "UserRoleId" INTEGER NOT NULL CONSTRAINT "PK_UserRole" PRIMARY KEY AUTOINCREMENT,
            "UserId" INTEGER NOT NULL,
            "RoleId" INTEGER NOT NULL,
            "EffectiveDate" TEXT NULL,
            "ExpiryDate" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        CREATE TABLE "Permission" (
            "PermissionId" INTEGER NOT NULL CONSTRAINT "PK_Permission" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "EntityName" TEXT NOT NULL,
            "EntityId" INTEGER NOT NULL,
            "PermissionName" TEXT NOT NULL,
            "RoleId" INTEGER NULL,
            "UserId" INTEGER NULL,
            "IsAuthorized" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        """ + LeafClusterDdl;

    // The five leaf clusters, each derived from its EntityBuilder plus every migration that altered it,
    // and then CROSS-CHECKED column-for-column against a real migrated PostgreSQL database (the
    // stomme-pg-44357 container the render guard uses). That second step is not ceremony — reading the
    // builders alone would have produced three wrong tables:
    //
    //   Language has NO "Name" column. The builder creates one, 05020401_RemoveLanguageName drops it for every
    //   provider but SQLite, 06000101_AddLanguageName restores it so the schemas match, and
    //   10000201_RemoveDeprecatedColumns finally drops it everywhere. The live database confirms 8 columns.
    //
    //   Log has NO audit columns at all — LogEntityBuilder extends BaseEntityBuilder, not the Auditable one.
    //
    //   Notification has no audit columns either, but DOES have the three IDeletable ones.
    //
    // Nullability is reproduced rather than tidied, exactly as the Setting table's IsPrivate note above says:
    // SiteTask.IsCompleted and Notification.IsRead are NULLABLE columns behind non-nullable CLR properties.
    private const string LeafClusterDdl = """

        CREATE TABLE "Language" (
            "LanguageId" INTEGER NOT NULL CONSTRAINT "PK_Language" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "Code" TEXT NOT NULL,
            "IsDefault" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        CREATE TABLE "Log" (
            "LogId" INTEGER NOT NULL CONSTRAINT "PK_Log" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NULL,
            "LogDate" TEXT NOT NULL,
            "PageId" INTEGER NULL,
            "ModuleId" INTEGER NULL,
            "UserId" INTEGER NULL,
            "Url" TEXT NOT NULL,
            "Server" TEXT NOT NULL,
            "Category" TEXT NOT NULL,
            "Feature" TEXT NOT NULL,
            "Function" TEXT NOT NULL,
            "Level" TEXT NOT NULL,
            "Message" TEXT NOT NULL,
            "MessageTemplate" TEXT NOT NULL,
            "Exception" TEXT NULL,
            "Properties" TEXT NULL,
            "Instance" TEXT NULL,
            "RemoteIPAddress" TEXT NULL
        );
        CREATE TABLE "Profile" (
            "ProfileId" INTEGER NOT NULL CONSTRAINT "PK_Profile" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NULL,
            "Name" TEXT NOT NULL,
            "Title" TEXT NOT NULL,
            "Description" TEXT NULL,
            "Category" TEXT NOT NULL,
            "ViewOrder" INTEGER NOT NULL,
            "MaxLength" INTEGER NOT NULL,
            "DefaultValue" TEXT NULL,
            "IsRequired" INTEGER NOT NULL,
            "IsPrivate" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "Options" TEXT NULL,
            "Validation" TEXT NULL,
            "Rows" INTEGER NULL,
            "Autocomplete" TEXT NULL
        );
        CREATE TABLE "SiteTask" (
            "SiteTaskId" INTEGER NOT NULL CONSTRAINT "PK_SiteTask" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "Type" TEXT NOT NULL,
            "Parameters" TEXT NULL,
            "IsCompleted" INTEGER NULL,
            "Status" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );
        CREATE TABLE "Notification" (
            "NotificationId" INTEGER NOT NULL CONSTRAINT "PK_Notification" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "FromUserId" INTEGER NULL,
            "ToUserId" INTEGER NULL,
            "ToEmail" TEXT NOT NULL,
            "ParentId" INTEGER NULL,
            "Subject" TEXT NOT NULL,
            "Body" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "IsDelivered" INTEGER NOT NULL,
            "DeliveredOn" TEXT NULL,
            "DeletedBy" TEXT NULL,
            "DeletedOn" TEXT NULL,
            "IsDeleted" INTEGER NOT NULL,
            "FromDisplayName" TEXT NULL,
            "FromEmail" TEXT NULL,
            "ToDisplayName" TEXT NULL,
            "SendOn" TEXT NULL,
            "IsRead" INTEGER NULL
        );

        CREATE TABLE "SiteGroup" (
            "SiteGroupId" INTEGER NOT NULL CONSTRAINT "PK_SiteGroup" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Type" TEXT NOT NULL,
            "PrimarySiteId" INTEGER NOT NULL,
            "Synchronize" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );

        CREATE TABLE "SiteGroupMember" (
            "SiteGroupMemberId" INTEGER NOT NULL CONSTRAINT "PK_SiteGroupMember" PRIMARY KEY AUTOINCREMENT,
            "SiteGroupId" INTEGER NOT NULL,
            "SiteId" INTEGER NOT NULL,
            "SynchronizedOn" TEXT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );

        CREATE UNIQUE INDEX "IX_SiteGroupMember" ON "SiteGroupMember" ("SiteId", "SiteGroupId");

        CREATE TABLE "Visitor" (
            "VisitorId" INTEGER NOT NULL CONSTRAINT "PK_Visitor" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "UserId" INTEGER NULL,
            "Visits" INTEGER NOT NULL,
            "IPAddress" TEXT NOT NULL,
            "UserAgent" TEXT NOT NULL,
            "Language" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "VisitedOn" TEXT NOT NULL,
            "Referrer" TEXT NULL,
            "Url" TEXT NULL
        );

        CREATE TABLE "UrlMapping" (
            "UrlMappingId" INTEGER NOT NULL CONSTRAINT "PK_UrlMapping" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "Url" TEXT NOT NULL,
            "MappedUrl" TEXT NOT NULL,
            "Requests" INTEGER NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "RequestedOn" TEXT NOT NULL,
            "Referrer" TEXT NULL,
            "UrlNormalized" TEXT NULL
        );

        -- STILL on ("SiteId", "Url") and still case-SENSITIVE. UrlNormalized was added as a LOOKUP column and
        -- deliberately did NOT move this index onto it: 'Foo/Bar' and 'foo/bar' are a legal pair today, and
        -- forbidding them would be a silent behaviour change — exactly the choice made for IX_Folder/IX_File.
        CREATE UNIQUE INDEX "IX_UrlMapping" ON "UrlMapping" ("SiteId", "Url");

        CREATE TABLE "Folder" (
            "FolderId" INTEGER NOT NULL CONSTRAINT "PK_Folder" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "ParentId" INTEGER NULL,
            "Name" TEXT NOT NULL,
            "Path" TEXT NOT NULL,
            "Order" INTEGER NOT NULL,
            "IsSystem" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "Type" TEXT NULL,
            "Capacity" INTEGER NULL,
            "ImageSizes" TEXT NULL,
            "CacheControl" TEXT NULL,
            "PathNormalized" TEXT NULL
        );

        -- STILL on ("SiteId", "Path") and still case-SENSITIVE. PathNormalized was added as a LOOKUP column
        -- and deliberately did NOT move this index onto it: 'Docs/' and 'docs/' are a legal pair today, and
        -- forbidding them would be a silent behaviour change. It is also why the render path's folder
        -- resolution returns a LIST rather than a single id.
        CREATE UNIQUE INDEX "IX_Folder" ON "Folder" ("SiteId", "Path");

        -- File — the last framework cluster onto the seam. TWELVE columns, matching the migrated
        -- PostgreSQL database exactly: NO is_deleted/deleted_by/deleted_on, because three migrations take the
        -- deletable trio away again even though FileEntityBuilder still calls AddDeletableColumns. Description
        -- is real despite the builder omitting it.
        CREATE TABLE "File" (
            "FileId" INTEGER NOT NULL CONSTRAINT "PK_File" PRIMARY KEY AUTOINCREMENT,
            "FolderId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "Extension" TEXT NOT NULL,
            "Size" INTEGER NOT NULL,
            "ImageHeight" INTEGER NOT NULL,
            "ImageWidth" INTEGER NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "Description" TEXT NULL,
            "NameNormalized" TEXT NULL
        );

        -- As IX_Folder: on ("FolderId", "Name"), case-SENSITIVE, NOT on the normalized column. 'Logo.png' and
        -- 'logo.png' may coexist in one folder exactly as they could before the normalized column.
        CREATE UNIQUE INDEX "IX_File" ON "File" ("FolderId", "Name");

        -- The SearchContent cluster — FOUR tables: a hand-rolled inverted index.
        --
        -- NOTE WHAT IS ABSENT FROM ALL FOUR: the audit quartet and the deletable trio. That is not an
        -- omission, it is the measurement — all four EntityBuilders extend BaseEntityBuilder (not
        -- DeletableAuditableBaseEntityBuilder) and the live PostgreSQL tables have exactly 13/4/6/3 columns
        -- with ZERO dropped stubs in pg_attribute. This is the FIRST cluster on the seam whose builders tell
        -- the truth (contrast Language, Folder and File). The CreatedOn/ModifiedOn columns
        -- below are PLAIN columns the application sets — there is no CreatedBy/ModifiedBy anywhere here to
        -- pair them with, so none of the four aggregates is IAuditable and none is IDeletable.
        CREATE TABLE "SearchContent" (
            "SearchContentId" INTEGER NOT NULL CONSTRAINT "PK_SearchContent" PRIMARY KEY AUTOINCREMENT,
            "SiteId" INTEGER NOT NULL,
            "EntityName" TEXT NOT NULL,
            "EntityId" TEXT NOT NULL,
            "Title" TEXT NOT NULL,
            "Description" TEXT NOT NULL,
            "Body" TEXT NOT NULL,
            "Url" TEXT NOT NULL,
            "Permissions" TEXT NOT NULL,
            "ContentModifiedBy" TEXT NOT NULL,
            "ContentModifiedOn" TEXT NOT NULL,
            "AdditionalContent" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL
        );

        CREATE UNIQUE INDEX "IX_SearchContent" ON "SearchContent" ("SiteId", "EntityName", "EntityId");

        CREATE TABLE "SearchContentProperty" (
            -- The key is PropertyId, NOT SearchContentPropertyId — harvested, not tidied.
            "PropertyId" INTEGER NOT NULL CONSTRAINT "PK_SearchContentProperty" PRIMARY KEY AUTOINCREMENT,
            "SearchContentId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "Value" TEXT NOT NULL
        );

        CREATE UNIQUE INDEX "IX_SearchContentProperty" ON "SearchContentProperty" ("SearchContentId", "Name");

        CREATE TABLE "SearchContentWord" (
            "SearchContentWordId" INTEGER NOT NULL CONSTRAINT "PK_SearchContentWord" PRIMARY KEY AUTOINCREMENT,
            "SearchContentId" INTEGER NOT NULL,
            "SearchWordId" INTEGER NOT NULL,
            "Count" INTEGER NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL
        );

        CREATE UNIQUE INDEX "IX_SearchContentWord" ON "SearchContentWord" ("SearchContentId", "SearchWordId");

        CREATE TABLE "SearchWord" (
            "SearchWordId" INTEGER NOT NULL CONSTRAINT "PK_SearchWord" PRIMARY KEY AUTOINCREMENT,
            "Word" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL
        );

        -- UNIQUE on the word alone: the dictionary is tenant-wide, with no site column. RavenDB has no
        -- equivalent, as for every other unique index on this seam.
        CREATE UNIQUE INDEX "IX_SearchWord" ON "SearchWord" ("Word");

        CREATE TABLE "PageModule" (
            "PageModuleId" INTEGER NOT NULL CONSTRAINT "PK_PageModule" PRIMARY KEY AUTOINCREMENT,
            "PageId" INTEGER NOT NULL,
            "ModuleId" INTEGER NOT NULL,
            "Title" TEXT NOT NULL,
            "Pane" TEXT NOT NULL,
            "Order" INTEGER NOT NULL,
            "ContainerType" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "CreatedOn" TEXT NOT NULL,
            "ModifiedBy" TEXT NOT NULL,
            "ModifiedOn" TEXT NOT NULL,
            "DeletedBy" TEXT NULL,
            "DeletedOn" TEXT NULL,
            "IsDeleted" INTEGER NOT NULL,
            "EffectiveDate" TEXT NULL,
            "ExpiryDate" TEXT NULL,
            "Header" TEXT NULL,
            "Footer" TEXT NULL
        );
        """;
}
