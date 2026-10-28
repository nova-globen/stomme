using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Stomme.Shared;

namespace Stomme.Repository
{
    public interface ITenantRepository
    {
        Task<IEnumerable<Tenant>> GetTenantsAsync();
        Task<Tenant> AddTenantAsync(Tenant tenant);
        Task<Tenant> UpdateTenantAsync(Tenant tenant);
        Task<Tenant> GetTenantAsync(int tenantId);
        Task DeleteTenantAsync(int tenantId);
    }

    /// <summary>
    /// The Tenant cluster on the persistence seam (D52) — the MASTER half of the master-bookkeeping wave, and half of
    /// the routing core. Tenant-table access goes through <see cref="ITenantStore"/> (writes) and the MASTER read path
    /// <see cref="IMasterStoreSessionFactory"/> (reads); this class holds no <c>DbContext</c> and no
    /// <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MASTER, not tenant (D23's master half): Tenant is installation-wide — the registry of physical tenant
    /// databases, read on every request to select the tenant's persistence provider (<c>Tenant.DBType</c>) — so reads
    /// go through <see cref="IMasterStoreSessionFactory"/> (NOT the tenant <see cref="IFrameworkStoreSessionFactory"/>)
    /// and writes through a store bound to <see cref="MasterDBContext"/>.
    /// </para>
    /// <para>
    /// Reads NEVER go through the store (D19). The harvested <c>UpdateTenantAsync</c> read <c>oldTenant</c> via
    /// <c>AsNoTracking()</c> and then set <c>_db.Entry(tenant).State = Modified</c> — a tracked write that a
    /// compiler-clean port silently breaks. On the seam the old-name read is a MASTER session query and the write is
    /// an <c>Update</c> through the store attaching a fresh aggregate. The master-tenant-rename GUARD is preserved
    /// verbatim: it is an IN-MEMORY <see cref="StringComparison.OrdinalIgnoreCase"/> comparison of the two names, not
    /// a query translation, so it behaves identically on every provider.
    /// </para>
    /// <para>
    /// The "Tenants" cache is preserved: <see cref="GetTenantsAsync"/> caches the list, the mutating methods
    /// invalidate it, and <see cref="GetTenantAsync(int)"/> filters that cached list in memory exactly as the
    /// harvested code did.
    /// </para>
    /// </remarks>
    public class TenantRepository : ITenantRepository
    {
        private readonly ITenantStore _store;
        private readonly IMasterStoreSessionFactory _sessions;
        private readonly ICacheManager _cache;

        public TenantRepository(ITenantStore store, IMasterStoreSessionFactory sessions, ICacheManager cache)
        {
            _store = store;
            _sessions = sessions;
            _cache = cache;
        }

        // The read path on the seam (D19): reads go through a MASTER query session, never the store. The IQueryable
        // is materialized inside this method, so it never leaves the repository (D7).
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<Tenant>> GetTenantsAsync()
        {
            return await _cache.GetCacheAsync("Tenants", async entry =>
            {
                return (await QueryAsync(session => session.Query<TenantAggregate>().ToList())).ConvertAll(TenantMapper.ToModel);
            });
        }

        public async Task<Tenant> AddTenantAsync(Tenant tenant)
        {
            var aggregate = TenantMapper.ToAggregate(tenant, assignId: false);
            await _store.AddAsync(aggregate);
            TenantMapper.CopyPersistedValues(aggregate, tenant);
            await _cache.RemoveCacheAsync("Tenants");
            return tenant;
        }

        public async Task<Tenant> UpdateTenantAsync(Tenant tenant)
        {
            // Read the OLD name through a MASTER session (untracked, D19), not the passed-in tenant, whose Name is the
            // proposed new value. The guard below is an IN-MEMORY comparison — preserve it exactly; it is application
            // behaviour, not a query translation.
            var oldTenant = await QueryAsync(session =>
                session.Query<TenantAggregate>().FirstOrDefault(item => item.Id == tenant.TenantId));

            if (oldTenant != null && (oldTenant.Name.Equals(TenantNames.Master, StringComparison.OrdinalIgnoreCase) && !oldTenant.Name.Equals(tenant.Name)))
            {
                throw new InvalidOperationException("Unable to rename the master tenant.");
            }

            // The harvested _db.Entry(tenant).State = Modified becomes an Update through the store, which attaches the
            // mapped columns (Name/DBConnectionString/DBType/version/audit) as Modified. DbContextUtils re-stamps
            // ModifiedBy/On and preserves the CreatedBy/On the tenant round-tripped.
            await _store.UpdateAsync(TenantMapper.ToAggregate(tenant, assignId: true));
            await _cache.RemoveCacheAsync("Tenants");
            return tenant;
        }

        public async Task<Tenant> GetTenantAsync(int tenantId)
        {
            return (await GetTenantsAsync()).FirstOrDefault(item => item.TenantId == tenantId);
        }

        public async Task DeleteTenantAsync(int tenantId)
        {
            var tenant = await GetTenantAsync(tenantId);
            if (tenant != null && !tenant.Name.Equals(TenantNames.Master, StringComparison.OrdinalIgnoreCase))
            {
                // The row was read via the cached list (a session read), so a fresh aggregate is attached for the
                // delete rather than a tracked instance removed (D19).
                await _store.DeleteAsync(TenantMapper.ToAggregate(tenant, assignId: true));
            }

            await _cache.RemoveCacheAsync("Tenants");
        }
    }
}
