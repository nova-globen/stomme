#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Landing.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Landing.Persistence
{
    /// <summary>The Landing module's repository: the host's storage-blind repository, plus the site's one page.</summary>
    public interface ILandingPageRepository : IRepository<LandingPageAggregate, Guid>
    {
        /// <summary>The site's landing page, or null for a site that never had one (the store filters by the ambient tenant).</summary>
        Task<LandingPageAggregate?> FindForSiteAsync(int siteId, CancellationToken ct = default);
    }

    public sealed class LandingPageRepository(IRepository<LandingPageAggregate, Guid> inner) : ILandingPageRepository
    {
        public Task<LandingPageAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<LandingPageAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(LandingPageAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(LandingPageAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(LandingPageAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<LandingPageAggregate>> FindAsync(Expression<Func<LandingPageAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<LandingPageAggregate?> FindForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(page => page.SiteId == siteId, ct)).SingleOrDefault();
    }
}
