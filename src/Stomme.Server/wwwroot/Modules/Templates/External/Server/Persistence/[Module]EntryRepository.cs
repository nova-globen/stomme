using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;
using [Owner].Module.[Module].Domain;

namespace [Owner].Module.[Module].Persistence
{
    /// <summary>The module's repository: the host's storage-blind repository for the aggregate, plus the module's reads.</summary>
    public interface I[Module]EntryRepository : IRepository<[Module]Entry, Guid>
    {
        Task<IReadOnlyList<[Module]Entry>> ListForSiteAsync(int siteId, CancellationToken ct = default);
    }

    /// <summary>
    /// Filters through <c>FindAsync</c>, which every store translates - safe for equality and ranges on non-string
    /// values such as the site id. Filtering on TEXT differs between stores (case), so normalise a text column instead.
    /// </summary>
    public sealed class [Module]EntryRepository(IRepository<[Module]Entry, Guid> inner) : I[Module]EntryRepository
    {
        public Task<[Module]Entry?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<[Module]Entry>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync([Module]Entry aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync([Module]Entry aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync([Module]Entry aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<[Module]Entry>> FindAsync(Expression<Func<[Module]Entry, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<[Module]Entry>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(entry => entry.SiteId == siteId, ct)).OrderByDescending(entry => entry.CreatedUtc).ToList();
    }
}
