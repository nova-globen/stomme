using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZiggyCreatures.Caching.Fusion;

namespace Stomme.Tests
{
    // GetPermissionsAsync(siteId, entityName) takes a siteId parameter — SynchronizationJob and the admin
    // surfaces call it for sites OTHER than the one the ambient alias resolves to — but the cache entry was
    // keyed on the entity name alone under the ambient alias's SiteKey. The first site to warm the entry
    // therefore answered for every site on the tenant: a cross-site read served the wrong site's permissions
    // out of the cache, and a cross-site write invalidated the wrong entry.
    //
    // The cache here is the REAL CacheManager over an in-memory FusionCache (PassThroughCache structurally
    // cannot see this: it never caches), and the ambient alias is pinned to site 1 while site 2 is queried.
    public class PermissionCacheSiteScopeTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly IRelationalDatabase _database = new SqliteDatabase();

        // The Permission table exactly as the tenant migrations leave it (PermissionEntityBuilder.BuildTable
        // plus AddAuditableColumns), mirroring PermissionStoreBehaviourTests.
        private const string PermissionTableDdl = """
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
            """;

        private sealed class StoreContext(DbContextOptions options, IRelationalDatabase database) : DbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder builder) => builder.ConfigureFrameworkStore(database);
        }

        private sealed class StubTenantContext : ITenantContext
        {
            public TenantId? CurrentTenantId => TenantId.Default;
            public bool IsSystemScope => false;
        }

        // In-test store adapter: an EfRepository over ONE StoreContext, wrapped as the closed store the
        // repository consumes. Mirrors EfPermissionStore, which does the same over TenantDBContext.
        private sealed class TestPermissionStore(DbContext context, ITenantContext tenantContext) : IPermissionStore
        {
            private readonly IRepository<PermissionAggregate, int> _r = new EfRepository<PermissionAggregate, int>(context, new AggregateTracker(), tenantContext, []);
            public Task<PermissionAggregate?> GetByIdAsync(int id, System.Threading.CancellationToken ct = default) => _r.GetByIdAsync(id, ct);
            public Task<IReadOnlyList<PermissionAggregate>> GetAllAsync(System.Threading.CancellationToken ct = default) => _r.GetAllAsync(ct);
            public Task AddAsync(PermissionAggregate a, System.Threading.CancellationToken ct = default) => _r.AddAsync(a, ct);
            public Task UpdateAsync(PermissionAggregate a, System.Threading.CancellationToken ct = default) => _r.UpdateAsync(a, ct);
            public Task DeleteAsync(PermissionAggregate a, System.Threading.CancellationToken ct = default) => _r.DeleteAsync(a, ct);
        }

        private sealed class TestSessionFactory(Func<DbContext> context) : IFrameworkStoreSessionFactory
        {
            private readonly EfDocumentSessionFactory _factory = new(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = context
            });

            public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);
            public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
        }

        // GetAliasAsync returns the AMBIENT alias — pinned to site 1 so that querying site 2 exercises the
        // cross-site path. Everything else throws.
        private sealed class StubTenants(Alias alias) : ITenantManager
        {
            public Task<Alias> GetAliasAsync() => Task.FromResult(alias);
            public Task<Tenant> GetTenantAsync() => throw new NotImplementedException();
            public Tenant GetResolvedTenant() => throw new NotImplementedException();
            public Task SetAliasAsync(Alias alias) => throw new NotImplementedException();
            public Task SetAliasAsync(int tenantId, int siteId) => throw new NotImplementedException();
            public void SetTenant(Tenant tenant) => throw new NotImplementedException();
        }

        // The permissions under test are user permissions, so the role correlation never drops them; roles
        // are answered empty and the rest throws.
        private sealed class StubRoles : IRoleRepository
        {
            public Task<IEnumerable<Role>> GetRolesAsync(int siteId) => Task.FromResult<IEnumerable<Role>>([]);
            public Task<IEnumerable<Role>> GetRolesAsync(int siteId, bool includeGlobalRoles) => Task.FromResult<IEnumerable<Role>>([]);
            public Task<Role> AddRoleAsync(Role role) => throw new NotImplementedException();
            public Task<Role> UpdateRoleAsync(Role role) => throw new NotImplementedException();
            public Task<Role> GetRoleAsync(int roleId) => throw new NotImplementedException();
            public Task<Role> GetRoleAsync(int roleId, bool tracking) => throw new NotImplementedException();
            public Task DeleteRoleAsync(int roleId) => throw new NotImplementedException();
        }

        private readonly ITenantContext _tenantContext = new StubTenantContext();
        private readonly StubTenants _tenants = new(new Alias { TenantId = 1, SiteId = 1 });

        public PermissionCacheSiteScopeTests()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandText = PermissionTableDdl;
            command.ExecuteNonQuery();
        }

        public void Dispose() => _connection.Dispose();

        private StoreContext NewContext()
        {
            var builder = new DbContextOptionsBuilder();
            builder.UseSqlite(_connection);
            return new StoreContext(builder.Options, _database);
        }

        private IPermissionStore NewStore() => new TestPermissionStore(NewContext(), _tenantContext);

        // One REAL cache shared across repository instances, as the singleton FusionCache is shared across
        // request scopes. Each repository gets a fresh store, as a transient registration hands out.
        private static CacheManager NewCacheManager() => new(new FusionCache(new FusionCacheOptions()));

        private PermissionRepository NewRepository(ICacheManager cache) =>
            new(NewStore(), new TestSessionFactory(NewContext), new StubRoles(), _tenants, cache);

        private static Permission NewPermission(int siteId, string entityName, int entityId, string permissionName, int userId) => new()
        {
            SiteId = siteId,
            EntityName = entityName,
            EntityId = entityId,
            PermissionName = permissionName,
            UserId = userId,
            IsAuthorized = true,
            CreatedBy = "installer",
            CreatedOn = DateTime.UtcNow,
            ModifiedBy = "installer",
            ModifiedOn = DateTime.UtcNow
        };

        private async Task SeedAsync(Permission permission) =>
            await NewStore().AddAsync(PermissionMapper.ToAggregate(permission, assignId: false));

        // The discriminating read: site 1 (the ambient alias's site) warms the cache, then site 2 is asked
        // for the same entity name. Keyed on the entity name alone, the second call is a cache HIT on site
        // 1's entry — the wrong site's permissions, silently.
        [Fact]
        public async Task Permissions_for_a_site_other_than_the_ambient_alias_get_their_own_cache_entry()
        {
            await SeedAsync(NewPermission(1, EntityNames.Page, 1, "View", userId: 5));
            await SeedAsync(NewPermission(2, EntityNames.Page, 1, "View", userId: 6));
            var cache = NewCacheManager();

            var ambientSite = (await NewRepository(cache).GetPermissionsAsync(1, EntityNames.Page)).ToList();
            Assert.NotEmpty(ambientSite);
            Assert.All(ambientSite, item => Assert.Equal(1, item.SiteId));

            var otherSite = (await NewRepository(cache).GetPermissionsAsync(2, EntityNames.Page)).ToList();
            Assert.NotEmpty(otherSite);
            Assert.All(otherSite, item => Assert.Equal(2, item.SiteId));
        }

        // The write half of the same key: adding a permission for site 2 must invalidate SITE 2's entry, so
        // the next read sees the new row rather than the stale cached list. Pins that ClearCacheAsync carries
        // the siteId the read path keys on — mutation check: drop the siteId from the clear key and this
        // goes red while the read-path test above stays green.
        [Fact]
        public async Task A_write_for_a_site_invalidates_that_sites_cache_entry()
        {
            await SeedAsync(NewPermission(2, EntityNames.Page, 1, "View", userId: 6));
            var cache = NewCacheManager();

            var before = (await NewRepository(cache).GetPermissionsAsync(2, EntityNames.Page)).ToList();
            Assert.Single(before);

            await NewRepository(cache).AddPermissionAsync(NewPermission(2, EntityNames.Page, 1, "Edit", userId: 6));

            var after = (await NewRepository(cache).GetPermissionsAsync(2, EntityNames.Page)).ToList();
            Assert.Equal(2, after.Count);
        }
    }
}
