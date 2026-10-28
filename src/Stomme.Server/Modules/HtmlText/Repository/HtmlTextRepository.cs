using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// The HtmlText module's repository, on the persistence seam (D55) — the module twin of the framework's
    /// <c>PermissionRepository</c> (D24). Writes go through <see cref="IHtmlTextStore"/>, reads through
    /// <see cref="IHtmlTextStoreSessionFactory"/> (D19 — reads NEVER go through the store, or a later keyed write
    /// would attach a different instance with the same key and EF would reject it). This class holds no
    /// <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public interface IHtmlTextRepository
    {
        Task<IEnumerable<Models.HtmlText>> GetHtmlTextsAsync(int moduleId);
        Task<Models.HtmlText> GetHtmlTextAsync(int moduleId);
        Task<Models.HtmlText> AddHtmlTextAsync(Models.HtmlText htmlText);
        Task DeleteHtmlTextAsync(int htmlTextId);
    }

    /// <remarks>
    /// Every write method takes a FRESH store (<see cref="NewStore"/>; BL-193, measured 2026-10-02). The store was held for
    /// the repository's life, and an Interactive Server circuit keeps one for as long as the page is open: the rows a circuit
    /// ADDED stayed tracked, and once its saves passed the version limit the pruning deleted one of them through an instance
    /// read by a query session - a second instance with the same key, refused by EF Core. <see cref="IHtmlTextStore"/> is
    /// transient and owns its context, so each write starts with an empty change tracker (the framework's per-write-store
    /// pattern, D47). <c>SiteCircuitWriteTests</c> saves past the limit through one scope.
    /// </remarks>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class HtmlTextRepository : IHtmlTextRepository, ITransientService
    {
        private readonly IServiceProvider _services;
        private readonly IHtmlTextStoreSessionFactory _sessions;
        private readonly ISettingRepository _settingRepository;

        public HtmlTextRepository(IServiceProvider services, IHtmlTextStoreSessionFactory sessions, ISettingRepository settingRepository)
        {
            _services = services;
            _sessions = sessions;
            _settingRepository = settingRepository;
        }

        // A store over a fresh context for each write (BL-193; see the class remarks).
        private IHtmlTextStore NewStore() => _services.GetRequiredService<IHtmlTextStore>();

        // The read path on the seam (D19): reads open their own session so the store's change tracker stays clean.
        // The IQueryable is materialized inside this method, so it never leaves the repository (D7); the LINQ
        // operators are plain LINQ, not EF Core's async ones, which would bind the read to the relational provider.
        // The Where(ModuleId)/OrderByDescending(CreatedOn) compose identically on both providers — no string
        // equality, no .ToLower() (ModuleId is an int, CreatedOn a DateTime), so the D48/D49 case split cannot
        // reach this read (verified by driving a Raven render).
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

        public async Task<IEnumerable<Models.HtmlText>> GetHtmlTextsAsync(int moduleId)
        {
            return await QueryAsync(session => session.Query<HtmlTextAggregate>()
                .Where(item => item.ModuleId == moduleId)
                .ToList()
                .ConvertAll(HtmlTextMapper.ToModel));
        }

        public async Task<Models.HtmlText> GetHtmlTextAsync(int moduleId)
        {
            return await QueryAsync(session => HtmlTextMapper.ToModel(session.Query<HtmlTextAggregate>()
                .Where(item => item.ModuleId == moduleId)
                .OrderByDescending(item => item.CreatedOn)
                .FirstOrDefault()));
        }

        public async Task<Models.HtmlText> AddHtmlTextAsync(Models.HtmlText htmlText)
        {
            // Version pruning: keep the newest (Versions - 1) rows, because the row added below becomes the newest.
            // Read through the session (D19), delete the surplus through the store — each read aggregate is detached,
            // so DeleteAsync attaches it Deleted by key with a clean tracker (the PermissionRepository pattern).
            var versions = int.Parse(await _settingRepository.GetSettingValueAsync(EntityNames.Module, htmlText.ModuleId, "Versions", "5"));
            var store = NewStore();
            if (versions > 0)
            {
                var existing = await QueryAsync(session => session.Query<HtmlTextAggregate>()
                    .Where(item => item.ModuleId == htmlText.ModuleId)
                    .OrderByDescending(item => item.CreatedOn)
                    .ToList());

                for (int i = versions - 1; i < existing.Count; i++)
                {
                    await store.DeleteAsync(existing[i]);
                }
            }

            var aggregate = HtmlTextMapper.ToAggregate(htmlText, assignId: false);
            await store.AddAsync(aggregate);
            HtmlTextMapper.CopyPersistedValues(aggregate, htmlText);
            return htmlText;
        }

        public async Task DeleteHtmlTextAsync(int htmlTextId)
        {
            // The one read that legitimately goes through the store (D19): it deletes the very instance it loaded,
            // so tracking is what we want and no second instance is ever attached.
            var store = NewStore();
            var aggregate = await store.GetByIdAsync(htmlTextId);
            if (aggregate != null)
            {
                await store.DeleteAsync(aggregate);
            }
        }
    }
}
