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
    // The DRIVEN half of the Tenant + Alias cluster (D52) — the routing core, the FINAL master-bookkeeping cluster.
    // AliasRepository and TenantRepository exercised end-to-end against a real SQLite database whose schema is written
    // by hand to match the MASTER migration set (cross-checked column-for-column against live stomme-pg-44357, host
    // 45432) — NOT EnsureCreated, which would build the schema from the very model under test.
    //
    // The conformance suite proves the STORE and QUERY shapes on both providers. This proves what it structurally
    // cannot: the repository ORCHESTRATION, the IAuditable audit STAMPING through DbContextUtils (which the conformance
    // fixtures do not run), the `version`-column shadow past AggregateRoot.Version, the builder-omitted db_type /
    // is_default columns round-tripping, the master-tenant-rename GUARD (both directions), and — the point of this
    // cluster — the routing lookup GetAliasAsync(url), which resolves a request IN MEMORY with OrdinalIgnoreCase and
    // therefore has the SAME answer on every provider regardless of case. The context here overrides SaveChangesAsync
    // to call DbContextUtils exactly as MasterDBContext does.
    public sealed class AliasesAndTenantsOnTheSeamTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // Tenant + Alias exactly as the MASTER migration set leaves them, cross-checked column-for-column against the
        // live stomme-pg-44357 database (host 45432). tenant: 9 columns incl. NULLABLE version + db_type (db_type is
        // absent from TenantEntityBuilder — migration 02010001 adds it). alias: 9 columns incl. NULLABLE is_default
        // (absent from AliasEntityBuilder — migration 03000201 adds it); alias has NO version column. Both audit
        // quartets are NOT NULL — the guard DbContextUtils must satisfy.
        private const string SchemaDdl = """
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

        // No HttpContext: install/routing runs from the installer/background, so DbContextUtils stamps an empty
        // username and the current time — precisely the shape the live tables showed.
        private sealed class NoRequestAccessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; }
        }

        // Pass-through: each GetCacheAsync re-invokes the factory, so reads always see committed rows. The routing
        // lookup's auto-register path depends on RemoveCacheAsync being callable.
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

        // In-test store adapters: an EfRepository over ONE StampingMasterContext, wrapped as the closed store the
        // repository consumes. Mirror EfTenantStore/EfAliasStore, which do the same over MasterDBContext.
        private sealed class TestTenantStore(DbContext context, ITenantContext tenantContext) : ITenantStore
        {
            private readonly IRepository<TenantAggregate, int> _r = new EfRepository<TenantAggregate, int>(context, new AggregateTracker(), tenantContext, []);
            public Task<TenantAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _r.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<TenantAggregate>> GetAllAsync(CancellationToken ct = default) => _r.GetAllAsync(ct);
            public Task AddAsync(TenantAggregate a, CancellationToken ct = default) => _r.AddAsync(a, ct);
            public Task UpdateAsync(TenantAggregate a, CancellationToken ct = default) => _r.UpdateAsync(a, ct);
            public Task DeleteAsync(TenantAggregate a, CancellationToken ct = default) => _r.DeleteAsync(a, ct);
        }

        private sealed class TestAliasStore(DbContext context, ITenantContext tenantContext) : IAliasStore
        {
            private readonly IRepository<AliasAggregate, int> _r = new EfRepository<AliasAggregate, int>(context, new AggregateTracker(), tenantContext, []);
            public Task<AliasAggregate?> GetByIdAsync(int id, CancellationToken ct = default) => _r.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<AliasAggregate>> GetAllAsync(CancellationToken ct = default) => _r.GetAllAsync(ct);
            public Task AddAsync(AliasAggregate a, CancellationToken ct = default) => _r.AddAsync(a, ct);
            public Task UpdateAsync(AliasAggregate a, CancellationToken ct = default) => _r.UpdateAsync(a, ct);
            public Task DeleteAsync(AliasAggregate a, CancellationToken ct = default) => _r.DeleteAsync(a, ct);
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

        private readonly IHttpContextAccessor _accessor = new NoRequestAccessor();
        private readonly ITenantContext _tenantContext = new StubTenantContext();

        public AliasesAndTenantsOnTheSeamTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = SchemaDdl;
            command.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();

        private StampingMasterContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StampingMasterContext(builder.Options, _database, _accessor);
        }

        private ITenantStore NewTenantStore() => new TestTenantStore(NewContext(), _tenantContext);
        private IAliasStore NewAliasStore() => new TestAliasStore(NewContext(), _tenantContext);

        private ITenantRepository NewTenants() =>
            new TenantRepository(NewTenantStore(), new TestMasterSessions(NewContext), new PassThroughCache());

        private IAliasRepository NewAliases() =>
            new AliasRepository(NewAliasStore(), new TestMasterSessions(NewContext), new PassThroughCache());

        private static Tenant NewTenant(string name, string dbType, string version) => new()
        {
            Name = name,
            DBConnectionString = "DefaultConnection",
            DBType = dbType,
            Version = version
        };

        // ==================== TENANT ====================

        // Audit-stamp guard AND version/db_type round-trip in one. The tenant table's audit quartet is NOT NULL and
        // nothing sets it — the aggregate is built straight from the model, whose stamps are default. DbContextUtils
        // stamps them because TenantAggregate is IAuditable (hazard 2). db_type (the provider-selector, absent from the
        // builder) and the `version` string (past the AggregateRoot.Version uint shadow) both round-trip.
        [Fact]
        public async Task Adding_a_tenant_assigns_an_id_stamps_audit_and_round_trips_db_type_and_version()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);

            var tenant = await NewTenants().AddTenantAsync(NewTenant("Contoso", "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", "5.0.0"));

            Assert.True(tenant.TenantId > 0);              // the database assigned the identity
            Assert.True(tenant.CreatedOn >= before);       // DbContextUtils stamped it — NOT default(DateTime)
            Assert.True(tenant.ModifiedOn >= before);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            var reloaded = session.Query<TenantAggregate>().First(item => item.Id == tenant.TenantId);
            Assert.Equal("Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", reloaded.DBType); // provider-selector, builder-omitted
            Assert.Equal("5.0.0", reloaded.TenantVersion);  // the `version` column, past the concurrency-token shadow
        }

        // GetTenantsAsync/GetTenantAsync read through the master session and the cached list.
        [Fact]
        public async Task Tenants_round_trip_through_the_repository_read_path()
        {
            await NewTenants().AddTenantAsync(NewTenant("Master", "sqlite", "5.0.0"));
            await NewTenants().AddTenantAsync(NewTenant("Contoso", "postgres", "5.0.0"));

            var all = (await NewTenants().GetTenantsAsync()).ToList();
            Assert.Equal(2, all.Count);

            var contoso = all.First(item => item.Name == "Contoso");
            var byId = await NewTenants().GetTenantAsync(contoso.TenantId);
            Assert.NotNull(byId);
            Assert.Equal("Contoso", byId!.Name);
        }

        // UpdateTenantAsync re-stamps ModifiedOn, persists the mapped columns (D19: the harvested tracked-write becomes
        // a store Update), and preserves CreatedOn.
        [Fact]
        public async Task Updating_a_tenant_restamps_modified_on_and_persists_columns()
        {
            var tenant = await NewTenants().AddTenantAsync(NewTenant("Contoso", "sqlite", "4.0.0"));
            var stampedAt = tenant.ModifiedOn;

            await Task.Delay(10);
            tenant.DBType = "Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server";
            tenant.Version = "5.0.0";
            await NewTenants().UpdateTenantAsync(tenant);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            var reloaded = session.Query<TenantAggregate>().First(item => item.Id == tenant.TenantId);
            Assert.Equal("Stomme.Database.PostgreSQL.PostgreSQLDatabase, Stomme.Server", reloaded.DBType);
            Assert.Equal("5.0.0", reloaded.TenantVersion);
            Assert.True(reloaded.ModifiedOn > stampedAt);
            Assert.Equal(tenant.CreatedOn, reloaded.CreatedOn, TimeSpan.FromSeconds(1)); // CreatedOn preserved on update
        }

        // THE master-tenant-rename guard (both directions), preserved verbatim from the harvested code and now an
        // IN-MEMORY OrdinalIgnoreCase comparison of the old name vs the proposed one — NOT a query translation.
        [Fact]
        public async Task Renaming_the_master_tenant_is_rejected_but_other_edits_to_it_are_allowed()
        {
            var master = await NewTenants().AddTenantAsync(NewTenant(TenantNames.Master, "sqlite", "5.0.0"));

            // Renaming Master throws.
            master.Name = "NotMaster";
            await Assert.ThrowsAsync<InvalidOperationException>(() => NewTenants().UpdateTenantAsync(master));

            // A non-rename edit to Master (change DBType, keep the name) is allowed.
            master.Name = TenantNames.Master;
            master.DBType = "postgres";
            var updated = await NewTenants().UpdateTenantAsync(master);
            Assert.Equal("postgres", updated.DBType);

            // Renaming a NON-master tenant is allowed.
            var contoso = await NewTenants().AddTenantAsync(NewTenant("Contoso", "sqlite", "5.0.0"));
            contoso.Name = "Fabrikam";
            var renamed = await NewTenants().UpdateTenantAsync(contoso);
            Assert.Equal("Fabrikam", renamed.Name);
        }

        // DeleteTenantAsync removes a normal tenant but REFUSES to delete the master tenant (the guard).
        [Fact]
        public async Task Deleting_a_tenant_removes_it_except_the_master_tenant()
        {
            var master = await NewTenants().AddTenantAsync(NewTenant(TenantNames.Master, "sqlite", "5.0.0"));
            var contoso = await NewTenants().AddTenantAsync(NewTenant("Contoso", "sqlite", "5.0.0"));

            await NewTenants().DeleteTenantAsync(master.TenantId);
            await NewTenants().DeleteTenantAsync(contoso.TenantId);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            var remaining = session.Query<TenantAggregate>().ToList();
            Assert.Single(remaining);
            Assert.Equal(TenantNames.Master, remaining[0].Name); // master survived the guard; contoso was deleted
        }

        // ==================== ALIAS ====================

        // Audit-stamp guard AND the builder-omitted is_default round-trip. AddAliasAsync lower-cases and strips the
        // protocol from the stored name (in C#, not a query), assigns the id, and DbContextUtils stamps the audit
        // quartet because AliasAggregate is IAuditable.
        [Fact]
        public async Task Adding_an_alias_normalizes_the_name_stamps_audit_and_round_trips_is_default()
        {
            var before = DateTime.UtcNow.AddSeconds(-1);

            var alias = await NewAliases().AddAliasAsync(new Alias { Name = "https://Stomme.ME", TenantId = 3, SiteId = 7, IsDefault = true });

            Assert.True(alias.AliasId > 0);
            Assert.Equal("stomme.me", alias.Name);       // protocol stripped, lower-cased — in C#
            Assert.True(alias.CreatedOn >= before);

            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            var reloaded = session.Query<AliasAggregate>().First(item => item.Id == alias.AliasId);
            Assert.Equal("stomme.me", reloaded.Name);
            Assert.Equal(3, reloaded.TenantId);
            Assert.Equal(7, reloaded.SiteId);
            Assert.True(reloaded.IsDefault);                // the is_default column, absent from the builder
        }

        // THE routing lookup — GetAliasAsync(url) — the reason this is the "last and DRIVEN" cluster. It resolves a
        // request URL to an alias ENTIRELY IN MEMORY with OrdinalIgnoreCase over the cached list, choosing the LONGEST
        // hierarchical match. Because the match never becomes a translated query, a mixed-case request resolves
        // identically on every provider — the EF-vs-RavenDB string-equality divergence (D48/D49) cannot reach it. THIS
        // is the brief's "single most important check", answered by mechanism: the comparison is not on the seam.
        [Fact]
        public async Task The_routing_lookup_resolves_the_longest_hierarchical_match_case_insensitively()
        {
            await NewAliases().AddAliasAsync(new Alias { Name = "stomme.me", TenantId = 3, SiteId = 7, IsDefault = true });
            await NewAliases().AddAliasAsync(new Alias { Name = "stomme.me/products", TenantId = 3, SiteId = 9, IsDefault = false });

            // A mixed-case request under the /products branch resolves to the longer alias, not the root.
            var resolved = await NewAliases().GetAliasAsync("STOMME.ME/products/widget/42");
            Assert.NotNull(resolved);
            Assert.Equal("stomme.me/products", resolved!.Name);
            Assert.Equal(9, resolved.SiteId);

            // A request that only matches the root resolves to the root.
            var root = await NewAliases().GetAliasAsync("Stomme.ME/about");
            Assert.NotNull(root);
            Assert.Equal("stomme.me", root!.Name);
            Assert.Equal(7, root.SiteId);
        }

        // The auto-register branch of the routing lookup: when every alias shares one tenant/site and none matches, a
        // root-domain alias is created on the fly through AddAliasAsync (a store write) and returned.
        [Fact]
        public async Task The_routing_lookup_auto_registers_a_root_alias_for_a_single_tenant_site()
        {
            await NewAliases().AddAliasAsync(new Alias { Name = "stomme.me", TenantId = 3, SiteId = 7, IsDefault = true });

            var resolved = await NewAliases().GetAliasAsync("newhost.example/some/path");
            Assert.NotNull(resolved);
            Assert.Equal("newhost.example", resolved!.Name); // root domain of the unmatched request
            Assert.Equal(3, resolved.TenantId);
            Assert.Equal(7, resolved.SiteId);

            // It was persisted, not just returned in memory.
            await using var session = new TestMasterSessions(NewContext).OpenQuerySession();
            Assert.Contains(session.Query<AliasAggregate>().ToList(), item => item.Name == "newhost.example");
        }

        // UpdateAliasAsync (D19: the harvested _db.Entry(alias).State = Modified becomes a store Update) plus GetById
        // and Delete round out the CRUD the AliasController and installer drive.
        [Fact]
        public async Task Updating_and_deleting_an_alias_go_through_the_store()
        {
            var alias = await NewAliases().AddAliasAsync(new Alias { Name = "stomme.me", TenantId = 3, SiteId = -1, IsDefault = true });

            // Update: the installer points the alias at the newly-created site.
            alias.SiteId = 11;
            await NewAliases().UpdateAliasAsync(alias);
            var afterUpdate = await NewAliases().GetAliasAsync(alias.AliasId);
            Assert.Equal(11, afterUpdate!.SiteId);

            // The untracked existence check the AliasController uses.
            Assert.NotNull(await NewAliases().GetAliasAsync(alias.AliasId, false));

            // Delete.
            await NewAliases().DeleteAliasAsync(alias.AliasId);
            Assert.Null(await NewAliases().GetAliasAsync(alias.AliasId));
        }

        // Deleting an unknown alias returns silently rather than NRE-ing (the deliberate, strictly-safer divergence
        // from the harvested _db.Alias.Remove(null)).
        [Fact]
        public async Task Deleting_an_unknown_alias_is_a_no_op()
        {
            await NewAliases().DeleteAliasAsync(999);
        }
    }
}
