using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Database.Sqlite;
using Stomme.Databases.Interfaces;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Stomme.Tests
{
    // The DRIVEN half of the Theme cluster: the ThemeRepository exercised end-to-end against a real SQLite
    // database whose schema is written by hand to match the MASTER migration set — not EnsureCreated, which would
    // build the schema from the very model under test.
    //
    // The conformance suite (Stomme.Persistence.Conformance.Tests) proves the STORE and QUERY shapes on both
    // providers. This proves the things it structurally cannot: the repository ORCHESTRATION, the IAuditable
    // audit STAMPING through DbContextUtils (which the conformance fixtures do not run — they use a plain context
    // and set the stamps as test data), the `version`-column shadow past AggregateRoot.Version, and the D19
    // read-then-write theme-sync shape (session read, store writes) on ONE store instance. The context here
    // overrides SaveChangesAsync to call DbContextUtils exactly as MasterDBContext does.
    public sealed class ThemesOnTheSeamTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // Theme exactly as the MASTER migration set leaves it, cross-checked column-for-column against the live
        // stomme-pg-44357 database (host 45432): EXACTLY eight columns — theme_id, theme_name, the audit quartet
        // (all NOT NULL), and NULLABLE name/version. The `version` string is what AggregateRoot.Version shadows.
        private const string ThemeSchemaDdl = """
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
            """;

        // Reproduces MasterDBContext: maps ConfigureMasterFrameworkStore AND runs the DbContextUtils audit hook.
        private sealed class StampingMasterContext(DbContextOptions options, IRelationalDatabase database, IHttpContextAccessor accessor) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureMasterFrameworkStore(database);

            public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            {
                DbContextUtils.SaveChanges(this, accessor);
                return base.SaveChangesAsync(cancellationToken);
            }
        }

        private sealed class StubTenantContext : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;
            public bool IsSystemScope => false;
        }

        // No HttpContext: theme sync runs from the installer/background, so DbContextUtils stamps an empty
        // username and the current time — precisely the shape the live theme table showed.
        private sealed class NoRequestAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        private sealed class PassThroughCache : ICacheManager
        {
            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory) => factory(CancellationToken.None);
            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? m, TimeSpan? d) => factory(CancellationToken.None);
            public Task RemoveCacheAsync(string key) => Task.CompletedTask;
            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory) => factory(CancellationToken.None);
            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory, TimeSpan? m, TimeSpan? d) => factory(CancellationToken.None);
            public Task RemoveCacheAsync(Alias alias, string key) => Task.CompletedTask;
            public Task ClearAllAsync() => Task.CompletedTask;
        }

        // In-test store adapter: an EfRepository over ONE StampingMasterContext, wrapped as the closed store the
        // repository consumes. Mirrors EfThemeStore, which does the same over MasterDBContext.
        private sealed class TestThemeStore(DbContext context, ITenantContext tenantContext) : IThemeStore
        {
            private readonly IRepository<ThemeAggregate, int> _r = new EfRepository<ThemeAggregate, int>(context, new AggregateTracker(), tenantContext, []);
            public Task<ThemeAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _r.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<ThemeAggregate>> GetAllAsync(CancellationToken ct = default) => _r.GetAllAsync(ct);
            public Task AddAsync(ThemeAggregate a, CancellationToken ct = default) => _r.AddAsync(a, ct);
            public Task UpdateAsync(ThemeAggregate a, CancellationToken ct = default) => _r.UpdateAsync(a, ct);
            public Task DeleteAsync(ThemeAggregate a, CancellationToken ct = default) => _r.DeleteAsync(a, ct);
        }

        private sealed class TestMasterSessions(Func<DbContext> context) : IMasterStoreSessionFactory
        {
            private readonly EfDocumentSessionFactory _factory = new(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = context
            });

            public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);
            public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
        }

        // Records the setting side-effects UpdateThemeAsync/DeleteThemeAsync drive, and returns null from
        // GetSettingAsync so the Update path takes the "add a new SiteEnabled setting" branch. Everything the
        // repository does not call throws, so a stub silently answering the wrong call would fail loudly.
        private sealed class RecordingSettings : ISettingRepository
        {
            public readonly List<Setting> Added = [];
            public readonly List<(string entityName, int entityId)> Deleted = [];

            public Task<Setting> GetSettingAsync(string entityName, int entityId, string settingName) => Task.FromResult<Setting>(null!);
            public Task<Setting> AddSettingAsync(Setting setting) { Added.Add(setting); return Task.FromResult(setting); }
            public Task DeleteSettingsAsync(string entityName, int entityId) { Deleted.Add((entityName, entityId)); return Task.CompletedTask; }

            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName) => throw new NotImplementedException();
            public Task<IEnumerable<Setting>> GetSettingsAsync(string entityName, int entityId) => throw new NotImplementedException();
            public Task<IEnumerable<Setting>> GetSettingsAsync(string e1, int i1, string e2, int i2) => throw new NotImplementedException();
            public Task<Setting> UpdateSettingAsync(Setting setting) => throw new NotImplementedException();
            public Task<Setting> GetSettingAsync(string entityName, int settingId) => throw new NotImplementedException();
            public Task DeleteSettingAsync(string entityName, int settingId) => throw new NotImplementedException();
            public Task<IEnumerable<string>> GetEntityNamesAsync() => throw new NotImplementedException();
            public Task<IEnumerable<int>> GetEntityIdsAsync(string entityName) => throw new NotImplementedException();
            public Task<string> GetSettingValueAsync(string entityName, int entityId, string settingName, string defaultValue) => throw new NotImplementedException();
        }

        // Records UpdatePermissionsAsync so the Update path's permission fan-out is proven to fire; the rest throw.
        private sealed class RecordingPermissions : IPermissionRepository
        {
            public readonly List<(int siteId, string entityName, int entityId)> Updated = [];

            public Task UpdatePermissionsAsync(int siteId, string entityName, int entityId, List<Permission> permissions, bool allowDeclaredKeyRows = false)
            {
                Updated.Add((siteId, entityName, entityId));
                return Task.CompletedTask;
            }

            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName) => throw new NotImplementedException();
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, string permissionName) => throw new NotImplementedException();
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId) => throw new NotImplementedException();
            public Task<IEnumerable<Permission>> GetPermissionsAsync(int siteId, string entityName, int entityId, string permissionName) => throw new NotImplementedException();
            public Task<Permission> AddPermissionAsync(Permission permission) => throw new NotImplementedException();
            public Task<Permission> UpdatePermissionAsync(Permission permission) => throw new NotImplementedException();
            public Task<Permission> GetPermissionAsync(int permissionId) => throw new NotImplementedException();
            public Task DeletePermissionAsync(int permissionId) => throw new NotImplementedException();
            public Task DeletePermissionsAsync(int siteId, string entityName, int entityId) => throw new NotImplementedException();
        }

        // GetAliasAsync returns an alias with a SiteKey (used to build the SiteEnabled setting name and the cache
        // key); everything else is store-touching and never called by the paths under test, so it throws.
        private sealed class StubTenants(Alias alias) : ITenantManager
        {
            public Task<Alias> GetAliasAsync() => Task.FromResult(alias);
            public Task<Tenant> GetTenantAsync() => throw new NotImplementedException();
            public Tenant GetResolvedTenant() => throw new NotImplementedException();
            public Task SetAliasAsync(Alias alias) => throw new NotImplementedException();
            public Task SetAliasAsync(int tenantId, int siteId) => throw new NotImplementedException();
            public void SetTenant(Tenant tenant) => throw new NotImplementedException();
        }

        private readonly IHttpContextAccessor _accessor = new NoRequestAccessor();
        private readonly ITenantContext _tenantContext = new StubTenantContext();
        private readonly RecordingSettings _settings = new();
        private readonly RecordingPermissions _permissions = new();
        private readonly StubTenants _tenants = new(new Alias { TenantId = 1, SiteId = 1 });

        public ThemesOnTheSeamTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = ThemeSchemaDdl;
            command.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();

        private StampingMasterContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StampingMasterContext(builder.Options, _database, _accessor);
        }

        private IThemeStore NewStore() => new TestThemeStore(NewContext(), _tenantContext);

        private IThemeRepository NewThemeRepository() =>
            new ThemeRepository(NewStore(), new TestMasterSessions(NewContext), new PassThroughCache(), _permissions, _tenants, _settings);

        private static Theme NewTheme(string themeName, string name, string version) => new()
        {
            ThemeName = themeName,
            Name = name,
            Version = version
        };

        // Seeds a theme through the store and returns the persisted model (id + stamps filled in).
        private async Task<Theme> SeedThemeAsync(string themeName, string name, string version)
        {
            var aggregate = ThemeMapper.ToAggregate(NewTheme(themeName, name, version), assignId: false);
            await NewStore().AddAsync(aggregate);
            return ThemeMapper.ToModel(aggregate);
        }

        // THE audit-stamp guard AND the version-shadow guard in one. The theme table's audit quartet is NOT NULL
        // and nothing sets it — the aggregate is built straight from the model, whose stamps are default.
        // DbContextUtils stamps them because ThemeAggregate is IAuditable (hazard 2). And the `version` string
        // must survive the AggregateRoot.Version uint shadow: read back through the session it is the value
        // written, not null and not a uint.
        [Fact]
        public async Task Adding_a_theme_assigns_an_id_stamps_audit_and_round_trips_the_version_column()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);
            var aggregate = ThemeMapper.ToAggregate(NewTheme("Stomme.Themes.StommeTheme, Stomme.Client", "Stomme Theme", "4.2.1"), assignId: false);

            await NewStore().AddAsync(aggregate);

            Assert.True(aggregate.Id > 0);                 // the database assigned the identity
            Assert.True(aggregate.CreatedOn >= before);    // DbContextUtils stamped it — NOT default(DateTime)
            Assert.True(aggregate.ModifiedOn >= before);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            var reloaded = session.Query<ThemeAggregate>().FirstOrDefault(item => item.ThemeName == "Stomme.Themes.StommeTheme, Stomme.Client");
            Assert.NotNull(reloaded);
            Assert.Equal("Stomme Theme", reloaded!.Name);
            Assert.Equal("4.2.1", reloaded.ThemeVersion); // the `version` column, past the concurrency-token shadow
        }

        // UpdateThemeAsync: re-stamps ModifiedOn, persists the mapped columns, fans permissions out and syncs the
        // per-site SiteEnabled setting. A fresh repository per call, so the update store never Added the row —
        // the production shape (the theme pre-exists from a prior run).
        [Fact]
        public async Task Updating_a_theme_restamps_modified_on_persists_the_version_and_syncs_the_enabled_setting()
        {
            var theme = await SeedThemeAsync("Stomme.Themes.StommeTheme, Stomme.Client", "Stomme Theme", "4.2.1");
            var stampedAt = theme.ModifiedOn;

            await Task.Delay(10);
            theme.SiteId = 7;
            theme.Version = "4.3.0";
            theme.IsEnabled = true;
            theme.PermissionList = [];
            await NewThemeRepository().UpdateThemeAsync(theme);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            var reloaded = session.Query<ThemeAggregate>().First(item => item.Id == theme.ThemeId);
            Assert.Equal("4.3.0", reloaded.ThemeVersion);
            Assert.True(reloaded.ModifiedOn > stampedAt);
            // CreatedOn is preserved on update (DbContextUtils only re-stamps Modified* for a Modified entity).
            Assert.Equal(theme.CreatedOn, reloaded.CreatedOn, TimeSpan.FromSeconds(1));

            // The side-effects fired: permissions fanned out, and the SiteEnabled setting was added (GetSetting
            // returned null).
            Assert.Contains(_permissions.Updated, item => item.entityName == EntityNames.Theme && item.entityId == theme.ThemeId);
            Assert.Contains(_settings.Added, item => item.EntityName == EntityNames.Theme && item.EntityId == theme.ThemeId && item.SettingValue == "True");
        }

        [Fact]
        public async Task Deleting_a_theme_removes_the_row_and_its_settings()
        {
            var theme = await SeedThemeAsync("Stomme.Themes.StommeTheme, Stomme.Client", "Stomme Theme", "4.2.1");

            await NewThemeRepository().DeleteThemeAsync(theme.ThemeId);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            Assert.Empty(session.Query<ThemeAggregate>().Where(item => item.Id == theme.ThemeId).ToList());
            Assert.Contains(_settings.Deleted, item => item.entityName == EntityNames.Theme && item.entityId == theme.ThemeId);
        }

        // DeleteThemeAsync on an unknown id returns silently rather than NRE-ing (the deliberate, strictly-safer
        // divergence from the harvested _db.Theme.Remove(null)).
        [Fact]
        public async Task Deleting_an_unknown_theme_is_a_no_op()
        {
            await NewThemeRepository().DeleteThemeAsync(999);
            Assert.Contains(_settings.Deleted, item => item.entityId == 999);
        }

        // THE D19 / hazard-6 guard, distilled from ProcessThemesAsync: on ONE store instance, ADD a newly
        // discovered theme AND UPDATE a DIFFERENT pre-existing theme that was read through the session (never
        // Added by this store). The harvested code mutated tracked entities; the seam reads through a session
        // (untracked) and attaches fresh aggregates for the writes, so the two writes touch distinct keys and no
        // identity conflict is raised. Verified by mutation — this test IS the mutation, not a call-graph argument.
        [Fact]
        public async Task A_sync_pass_adds_a_new_theme_and_updates_an_existing_one_on_one_store_without_conflict()
        {
            // A theme that already exists in the database (a prior run's row).
            var existing = await SeedThemeAsync("Stomme.Themes.Existing, Stomme.Client", "Existing", "1.0.0");

            var store = NewStore(); // ONE store instance for BOTH writes, as ProcessThemesAsync uses.

            // Read the existing theme through a SESSION (untracked), then mutate the detached model.
            Theme detached;
            await using (var session = new TestMasterSessions(NewContext).OpenQuerySession())
            {
                detached = ThemeMapper.ToModel(session.Query<ThemeAggregate>().First(item => item.Id == existing.ThemeId));
            }
            detached.Version = "1.1.0";

            // ADD a brand-new theme AND UPDATE the existing one through the SAME store — distinct keys, so no
            // identity conflict even though the store never Added the existing row.
            await store.AddAsync(ThemeMapper.ToAggregate(NewTheme("Stomme.Themes.New, Stomme.Client", "New", "2.0.0"), assignId: false));
            await store.UpdateAsync(ThemeMapper.ToAggregate(detached, assignId: true));

            await using var verify = new TestMasterSessions(NewContext).OpenQuerySession();
            var all = verify.Query<ThemeAggregate>().ToList();
            Assert.Equal(2, all.Count);
            Assert.Equal("1.1.0", all.First(item => item.Id == existing.ThemeId).ThemeVersion);
            Assert.Contains(all, item => item.ThemeName == "Stomme.Themes.New, Stomme.Client" && item.ThemeVersion == "2.0.0");
        }
    }
}
