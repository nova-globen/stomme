using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface ISiteGroupRepository
    {
        Task<IEnumerable<SiteGroup>> GetSiteGroupsAsync();
        Task<SiteGroup> AddSiteGroupAsync(SiteGroup siteGroup);
        Task<SiteGroup> UpdateSiteGroupAsync(SiteGroup siteGroup);
        Task<SiteGroup> GetSiteGroupAsync(int siteGroupId);
        Task<SiteGroup> GetSiteGroupAsync(int siteGroupId, bool tracking);
        Task DeleteSiteGroupAsync(int siteGroupId);
    }

    /// <summary>
    /// The SiteGroup cluster on the persistence seam. SiteGroup-table access goes through
    /// <see cref="ISiteGroupStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Landed together with <see cref="SiteGroupMemberRepository"/> — see <see cref="SiteGroupAggregate"/> for
    /// the model error that made "together" mandatory rather than tidy. No repository edges in either
    /// direction, one table, tenant-side only: so one store, no <c>IsMaster</c> routing, and no cycle to check.
    /// </para>
    /// <para>
    /// <b>The <c>tracking</c> parameter is now inert, and that is a real semantic change worth stating.</b>
    /// The harvested <c>GetSiteGroupAsync(id, tracking)</c> returned an entity attached to a context that was
    /// disposed on the way out of the method, so "tracking: true" bought the caller nothing it could use — the
    /// context was already gone. On the seam a session read is never tracked, so both arms collapse to
    /// the same read. The parameter is retained because <c>SiteGroupController.Put</c> passes it, and is
    /// documented as inert rather than removed, which would be a contract change this increment did not
    /// measure. Pinned by <c>The_tracking_parameter_returns_the_same_detached_group_either_way</c>.
    /// </para>
    /// </remarks>
    public class SiteGroupRepository : ISiteGroupRepository
    {
        private readonly ISiteGroupStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public SiteGroupRepository(ISiteGroupStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and a later Update would then attach a DIFFERENT instance with the same key, which EF rejects.
        // SynchronizationJob walks straight into it — it reads every group, then writes each one back
        // (Synchronize = false) on ONE repository instance, hence one store and one context. A query session
        // opens its own context, so reads leave the store's change tracker clean.
        //
        // The IQueryable is materialized inside this method, so it never leaves the repository. The LINQ
        // operators are plain LINQ rather than EF Core's async ones, which would bind these reads to the
        // relational provider.
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

        public async Task<IEnumerable<SiteGroup>> GetSiteGroupsAsync()
        {
            // Unfiltered, exactly as db.SiteGroup.ToListAsync() was — SynchronizationJob filters the result in
            // memory by Synchronize and Type, and it did so before this cluster moved too.
            return await QueryAsync(session => session.Query<SiteGroupAggregate>()
                .ToList()
                .ConvertAll(SiteGroupMapper.ToModel));
        }

        public async Task<SiteGroup> AddSiteGroupAsync(SiteGroup siteGroup)
        {
            var aggregate = SiteGroupMapper.ToAggregate(siteGroup, assignId: false);
            await _store.AddAsync(aggregate);
            SiteGroupMapper.CopyPersistedValues(aggregate, siteGroup);
            return siteGroup;
        }

        public async Task<SiteGroup> UpdateSiteGroupAsync(SiteGroup siteGroup)
        {
            await _store.UpdateAsync(SiteGroupMapper.ToAggregate(siteGroup, assignId: true));
            return siteGroup;
        }

        public async Task<SiteGroup> GetSiteGroupAsync(int siteGroupId)
        {
            return await GetSiteGroupAsync(siteGroupId, true);
        }

        public async Task<SiteGroup> GetSiteGroupAsync(int siteGroupId, bool tracking)
        {
            // `tracking` is inert on the seam — see the class remarks. Both arms of the harvested if/else
            // collapse to the same session read, which is why the parameter is ignored rather than branched on.
            _ = tracking;

            return await QueryAsync(session => SiteGroupMapper.ToModel(session.Query<SiteGroupAggregate>()
                .FirstOrDefault(item => item.Id == siteGroupId)));
        }

        public async Task DeleteSiteGroupAsync(int siteGroupId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(siteGroupId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here (db.SiteGroup.Remove(null)). Returning
                // silently for an unknown id matches DeleteSetting/DeleteModule/DeleteRole/DeleteUser/
                // DeletePermission/DeleteLanguage's shape — a deliberate, strictly-safer divergence, consistent
                // across the migrated clusters.
                return;
            }

            // NOTE the cascade this does NOT perform. Deleting a group leaves its SiteGroupMember rows to the
            // DATABASE's ON DELETE CASCADE, which is exactly what happened before this cluster moved — EF never
            // cascaded it either (the members were not loaded), the foreign key did. On RavenDB there is no
            // such constraint and the members would be orphaned; that divergence is inherited from the seam,
            // not introduced here. See src/Stomme.Server/README.md (persistence seam).
            await _store.DeleteAsync(aggregate);
        }
    }
}
