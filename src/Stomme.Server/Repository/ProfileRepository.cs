using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface IProfileRepository
    {
        Task<IEnumerable<Profile>> GetProfilesAsync(int siteId);
        Task<Profile> AddProfileAsync(Profile profile);
        Task<Profile> UpdateProfileAsync(Profile profile);
        Task<Profile> GetProfileAsync(int profileId);
        Task<Profile> GetProfileAsync(int profileId, bool tracking);
        Task DeleteProfileAsync(int profileId);
    }

    /// <summary>
    /// The Profile cluster on the persistence seam. Profile-table access goes through
    /// <see cref="IProfileStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A LEAF cluster: one table, tenant-side only, no navigations. It has ONE INBOUND repository edge —
    /// <see cref="SiteRepository"/> takes <see cref="IProfileRepository"/> to seed a new site's eight default
    /// profile properties — which puts this cluster on the site-creation path (i.e. the installer). The edge
    /// cannot close a container cycle because this repository takes no repository at all, only a store and a
    /// session factory. Do not add one without re-running that check.
    /// </para>
    /// <para>
    /// These are profile property DEFINITIONS, not users' values for them — the values are Settings
    /// under <c>EntityNames.User</c>. See <see cref="ProfileAggregate"/>.
    /// </para>
    /// </remarks>
    public class ProfileRepository : IProfileRepository
    {
        private readonly IProfileStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public ProfileRepository(IProfileStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and UpdateProfile would then attach a DIFFERENT instance with the same key, which EF rejects.
        // ProfileController.Put walks straight into it — it reads to authorize and then writes, on ONE
        // repository instance. A query session opens its own context, so reads leave the store's tracker clean.
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

        public async Task<IEnumerable<Profile>> GetProfilesAsync(int siteId)
        {
            // Filtered in the DATABASE (rule 9). THE NULL ARM IS LOAD-BEARING: SiteId == null means a GLOBAL
            // profile property that every site shows. Drop it and nothing throws — the global properties
            // simply stop appearing on every profile form in the installation.
            return await QueryAsync(session => session.Query<ProfileAggregate>()
                .Where(item => item.SiteId == siteId || item.SiteId == null)
                .ToList()
                .ConvertAll(ProfileMapper.ToModel));
        }

        public async Task<Profile> AddProfileAsync(Profile profile)
        {
            var aggregate = ProfileMapper.ToAggregate(profile, assignId: false);
            await _store.AddAsync(aggregate);
            ProfileMapper.CopyPersistedValues(aggregate, profile);
            return profile;
        }

        public async Task<Profile> UpdateProfileAsync(Profile profile)
        {
            var aggregate = ProfileMapper.ToAggregate(profile, assignId: true);
            await _store.UpdateAsync(aggregate);
            ProfileMapper.CopyPersistedValues(aggregate, profile);
            return profile;
        }

        public async Task<Profile> GetProfileAsync(int profileId)
        {
            return await GetProfileAsync(profileId, true);
        }

        /// <summary>
        /// Reads one profile property. <paramref name="tracking"/> is now INERT — see the remarks.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The parameter is kept and deliberately ignored.</b> It existed to let
        /// <c>ProfileController.Put</c> read a profile to authorize the request and then write a DIFFERENT
        /// instance of the same row back, without EF complaining that two instances share a key —
        /// <c>tracking: false</c> was the workaround for exactly the tracked-instance conflict this seam removes
        /// structurally. Every read here now goes through a query session with its own context, so nothing
        /// this repository returns is ever tracked and both call sites are safe by construction.
        /// </para>
        /// <para>
        /// It is not deleted because it is on <see cref="IProfileRepository"/>, which is public API that
        /// downstream apps and modules bind to; removing it is a breaking change and belongs to whoever
        /// retires the flag across the framework, not to a storage move. It now behaves as the
        /// <c>tracking: false</c> path always did, which is the safe half of the old contract.
        /// </para>
        /// </remarks>
        public async Task<Profile> GetProfileAsync(int profileId, bool tracking)
        {
            return await QueryAsync(session => ProfileMapper.ToModel(session.Query<ProfileAggregate>()
                .FirstOrDefault(item => item.Id == profileId)));
        }

        public async Task DeleteProfileAsync(int profileId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(profileId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches the shape every migrated cluster settled on — a deliberate, strictly-safer
                // divergence.
                return;
            }

            await _store.DeleteAsync(aggregate);
        }
    }
}
