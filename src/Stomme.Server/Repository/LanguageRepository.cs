using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface ILanguageRepository
    {
        Task<IEnumerable<Language>> GetLanguagesAsync(int siteId);
        Task<Language> AddLanguageAsync(Language language);
        Task UpdateLanguageAsync(Language language);
        Task<Language> GetLanguageAsync(int languageId);
        Task DeleteLanguageAsync(int languageId);
    }

    /// <summary>
    /// The Language cluster on the persistence seam. Language-table access goes through
    /// <see cref="ILanguageStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A LEAF cluster: no repository edges in either direction, no navigations, one table, tenant-side only —
    /// so one store, no <c>IsMaster</c> routing, and no cycle to check.
    /// </para>
    /// <para>
    /// <b>THE ONE REAL HAZARD IN THIS CLUSTER, and the compiler cannot see it.</b> The harvested add/update
    /// enforced "at most one default language per site" like this:
    /// <code>
    ///     (await db.Language.Where(l =&gt; l.SiteId == language.SiteId).ToListAsync())
    ///         .ForEach(l =&gt; l.IsDefault = false);
    ///     ...
    ///     await db.SaveChangesAsync();
    /// </code>
    /// Those rows are TRACKED, so mutating them in a <c>ForEach</c> and calling <c>SaveChanges</c> later was
    /// enough to persist them — the write is invisible at the call site. Through the seam the read comes from a
    /// query SESSION whose context nothing tracks, so the identical <c>ForEach</c> would mutate detached
    /// objects and persist NOTHING: every language would silently keep <c>IsDefault</c>, the site would have
    /// several defaults, and no exception would be raised anywhere. The un-defaulting is therefore an EXPLICIT
    /// <see cref="ILanguageStore.UpdateAsync"/> per row below. Pinned by
    /// <c>Adding_a_default_language_un_defaults_the_others</c> and
    /// <c>Updating_a_language_to_default_un_defaults_the_others</c>.
    /// </para>
    /// <para>
    /// COST: that un-defaulting is now one round trip per row where the harvested code batched every row and
    /// the insert into a single <c>SaveChanges</c>. The seam has no unit of work spanning aggregates — the same
    /// regression accepted for <c>UpdatePermissions</c>. A site has a handful of languages, so the row
    /// count is small; the consequence that matters is not speed but atomicity: a failure part-way now leaves
    /// earlier rows un-defaulted. See src/Stomme.Server/README.md (persistence seam).
    /// </para>
    /// </remarks>
    public class LanguageRepository : ILanguageRepository
    {
        private readonly ILanguageStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public LanguageRepository(ILanguageStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and a later Update would then attach a DIFFERENT instance with the same key, which EF rejects.
        // UpdateLanguage walks straight into it — it reads the site's other languages and writes them back on
        // ONE repository instance, hence one store and one context. A query session opens its own context, so
        // reads leave the store's change tracker clean.
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

        public async Task<IEnumerable<Language>> GetLanguagesAsync(int siteId)
        {
            // Filtered in the DATABASE on SiteId, exactly as db.Language.Where did (rule 9).
            return await QueryAsync(session => session.Query<LanguageAggregate>()
                .Where(item => item.SiteId == siteId)
                .ToList()
                .ConvertAll(LanguageMapper.ToModel));
        }

        public async Task<Language> AddLanguageAsync(Language language)
        {
            if (language.IsDefault)
            {
                // Ensure all other languages are not set to default. See the class remarks: this was a
                // tracked-entity mutation and is now an explicit write per row, because a session read is not
                // tracked and the ForEach would otherwise persist nothing.
                await ClearDefaultAsync(language.SiteId, exceptLanguageId: null);
            }

            language.Name = ""; // stored in database but not used (SQLite limitation)

            var aggregate = LanguageMapper.ToAggregate(language, assignId: false);
            await _store.AddAsync(aggregate);
            LanguageMapper.CopyPersistedValues(aggregate, language);
            return language;
        }

        public async Task UpdateLanguageAsync(Language language)
        {
            // The harvested code guarded the write with `if (language.LanguageId != 0)` — an unsaved language
            // was never marked Modified and SaveChanges simply skipped it. Preserved: a keyed write of id 0
            // would be an UPDATE matching no row rather than a no-op, and on RavenDB a blind upsert to
            // Language/0 (see the guard below).
            if (language.LanguageId != 0)
            {
                await _store.UpdateAsync(LanguageMapper.ToAggregate(language, assignId: true));
            }

            if (language.IsDefault)
            {
                // Ensure all other languages are not set to default. Excludes THIS language, exactly as the
                // harvested `l.LanguageId != language.LanguageId` arm did — without it the language being made
                // default would immediately un-default itself.
                await ClearDefaultAsync(language.SiteId, exceptLanguageId: language.LanguageId);
            }

            language.Name = ""; // stored in database but not used (SQLite limitation)
        }

        /// <summary>
        /// Clears <see cref="Language.IsDefault"/> on every other language of the site — the invariant nothing
        /// in the schema enforces. Read through a session and written through the store, per row.
        /// </summary>
        private async Task ClearDefaultAsync(int? siteId, int? exceptLanguageId)
        {
            // The SiteId filter stays in the DATABASE (rule 9); reading through the store instead would mean
            // GetAllAsync, i.e. every language in the tenant. Only rows that are actually default are written,
            // which keeps this a no-op in the common case rather than rewriting the site's whole language set.
            var aggregates = await QueryAsync(session => session.Query<LanguageAggregate>()
                .Where(item => item.SiteId == siteId)
                .Where(item => item.IsDefault)
                .ToList());

            foreach (var aggregate in aggregates)
            {
                if (exceptLanguageId != null && aggregate.Id == exceptLanguageId)
                {
                    continue;
                }

                aggregate.IsDefault = false;

                // A detached aggregate read through a session, written through the store's own clean tracker
                // (so no tracked instance conflicts). One SaveChanges per row — see the class remarks for the cost this trades.
                await _store.UpdateAsync(aggregate);
            }
        }

        public async Task<Language> GetLanguageAsync(int languageId)
        {
            return await QueryAsync(session => LanguageMapper.ToModel(session.Query<LanguageAggregate>()
                .FirstOrDefault(item => item.Id == languageId)));
        }

        public async Task DeleteLanguageAsync(int languageId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(languageId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches DeleteSetting/DeleteModule/DeleteRole/DeleteUser/DeletePermission's shape — a
                // deliberate, strictly-safer divergence, consistent across the migrated clusters.
                return;
            }

            await _store.DeleteAsync(aggregate);
        }
    }
}
