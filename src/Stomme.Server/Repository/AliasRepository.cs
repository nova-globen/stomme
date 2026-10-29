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
    public interface IAliasRepository
    {
        Task<IEnumerable<Alias>> GetAliasesAsync();
        Task<Alias> AddAliasAsync(Alias alias);
        Task<Alias> UpdateAliasAsync(Alias alias);
        Task<Alias> GetAliasAsync(int aliasId);
        Task<Alias> GetAliasAsync(int aliasId, bool tracking);
        Task<Alias> GetAliasAsync(string url);
        Task DeleteAliasAsync(int aliasId);
    }

    /// <summary>
    /// The Alias cluster on the persistence seam (D52) — the MASTER half of the master-bookkeeping wave, and half of
    /// the routing core. Alias-table access goes through <see cref="IAliasStore"/> (writes) and the MASTER read path
    /// <see cref="IMasterStoreSessionFactory"/> (reads); this class holds no <c>DbContext</c> and no
    /// <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MASTER, not tenant (D23's master half): Alias is installation-wide routing state — the map from host+path to a
    /// Site and Tenant — so reads go through <see cref="IMasterStoreSessionFactory"/> (NOT the tenant
    /// <see cref="IFrameworkStoreSessionFactory"/>) and writes through a store bound to <see cref="MasterDBContext"/>.
    /// </para>
    /// <para>
    /// Reads NEVER go through the store (D19): a query session opens its own context, so the store's change tracker
    /// stays clean. The harvested <c>UpdateAliasAsync</c> set <c>_db.Entry(alias).State = Modified</c> — a tracked
    /// write — which on the seam becomes an <c>Update</c> through the store that attaches a fresh aggregate.
    /// </para>
    /// <para>
    /// <b>The routing lookup is unchanged and provider-neutral by construction.</b> <see cref="GetAliasAsync(string)"/>
    /// matches the request URL against the cached alias list ENTIRELY IN MEMORY with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> — it never issues a translated query, so the EF-vs-RavenDB
    /// string-equality divergence (D48/D49) cannot reach it. <see cref="AddAliasAsync"/>/<see cref="UpdateAliasAsync"/>
    /// lower-case the stored name in C# (not in a query), so the in-memory match sees lowercase on both sides.
    /// </para>
    /// </remarks>
    public class AliasRepository : IAliasRepository
    {
        private readonly IAliasStore _store;
        private readonly IMasterStoreSessionFactory _sessions;
        private readonly ICacheManager _cache;

        public AliasRepository(IAliasStore store, IMasterStoreSessionFactory sessions, ICacheManager cache)
        {
            _store = store;
            _sessions = sessions;
            _cache = cache;
        }

        // The read path on the seam (D19): reads go through a MASTER query session, never the store, so the store's
        // tracker stays clean. The IQueryable is materialized inside this method, so it never leaves the repository
        // (D7). Plain LINQ rather than EF's async operators, which would bind these reads to the relational provider.
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

        public async Task<IEnumerable<Alias>> GetAliasesAsync()
        {
            return await _cache.GetCacheAsync("Aliases", async entry =>
            {
                return (await QueryAsync(session => session.Query<AliasAggregate>().ToList())).ConvertAll(AliasMapper.ToModel);
            });
        }

        public async Task<Alias> AddAliasAsync(Alias alias)
        {
            alias.Name = alias.Name.Contains("://") ? alias.Name.Substring(alias.Name.IndexOf("://") + 3).ToLower() : alias.Name.ToLower();
            var aggregate = AliasMapper.ToAggregate(alias, assignId: false);
            await _store.AddAsync(aggregate);
            AliasMapper.CopyPersistedValues(aggregate, alias);
            await _cache.RemoveCacheAsync("Aliases");
            return alias;
        }

        public async Task<Alias> UpdateAliasAsync(Alias alias)
        {
            // The harvested code set _db.Entry(alias).State = Modified and saved; on the seam that is an Update through
            // the store, which attaches the mapped columns (Name/TenantId/SiteId/IsDefault/audit) as Modified.
            // DbContextUtils re-stamps ModifiedBy/On and preserves the CreatedBy/On the alias round-tripped.
            alias.Name = alias.Name.Contains("://") ? alias.Name.Substring(alias.Name.IndexOf("://") + 3).ToLower() : alias.Name.ToLower();
            await _store.UpdateAsync(AliasMapper.ToAggregate(alias, assignId: true));
            await _cache.RemoveCacheAsync("Aliases");
            return alias;
        }

        public async Task<Alias> GetAliasAsync(int aliasId)
        {
            return await GetAliasAsync(aliasId, true);
        }

        public async Task<Alias> GetAliasAsync(int aliasId, bool tracking)
        {
            // On the seam every read is an untracked session query (D19), so the harvested tracking flag is inert —
            // no caller mutates the returned instance and saves it through the same context any more; writes go
            // through the store. The parameter is kept so the interface and its one caller (AliasController's
            // existence check) are unchanged.
            return AliasMapper.ToModel(await QueryAsync(session =>
                session.Query<AliasAggregate>().FirstOrDefault(item => item.Id == aliasId)));
        }

        // lookup alias based on url - note that alias values are hierarchical
        public async Task<Alias> GetAliasAsync(string url)
        {
            Alias alias = null;

            List<Alias> aliases = (await GetAliasesAsync()).ToList();
            var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // iterate segments to find keywords
            int start = segments.Length;
            for (int i = 0; i < segments.Length; i++)
            {
                if (Constants.ReservedRoutes.Contains(segments[i]) || segments[i] == Constants.ModuleDelimiter)
                {
                    start = i;
                    break;
                }
            }

            // iterate segments in reverse order to find alias match
            for (int i = start; i > 0; i--)
            {
                alias = aliases.Find(item => item.Name.Equals(string.Join("/", segments, 0, i), StringComparison.OrdinalIgnoreCase));
                if (alias != null)
                {
                    break; // found a matching alias
                }
            }

            // No match is no alias. Only configured hosts are aliases (the install config and the admin UI); the
            // harvested code registered ANY unknown host as a new alias on a one-site installation, so a request with a
            // forged Host header wrote a row.
            return alias;
        }

        public async Task DeleteAliasAsync(int aliasId)
        {
            // The one read that legitimately goes through the store (D19): it deletes the very instance it loaded, so
            // tracking is what we want and no second instance is ever attached. The harvested code NRE'd on an unknown
            // id (Remove(null)); returning silently matches DeleteTheme/DeleteModuleDefinition's strictly-safer shape.
            var aggregate = await _store.GetByIdAsync(aliasId);
            if (aggregate != null)
            {
                await _store.DeleteAsync(aggregate);
            }
            await _cache.RemoveCacheAsync("Aliases");
        }
    }
}
