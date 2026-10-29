#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.CostOptimization.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.CostOptimization.Persistence
{
    /// <summary>
    /// The recommendations' closed repository over the storage-blind store. Every read is a SITE's: another
    /// site's recommendation does not exist for a request, so an id from another site reads as not found.
    /// </summary>
    public interface IOptimizationRecommendationRepository : IRepository<OptimizationRecommendationAggregate, Guid>
    {
        /// <summary>The site's recommendations, newest first.</summary>
        Task<IReadOnlyList<OptimizationRecommendationAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>The recommendation with this id when it is the site's; otherwise none.</summary>
        Task<OptimizationRecommendationAggregate?> FindForSiteAsync(int siteId, Guid id, CancellationToken ct = default);
    }

    public sealed class OptimizationRecommendationRepository(IRepository<OptimizationRecommendationAggregate, Guid> inner) : IOptimizationRecommendationRepository
    {
        public Task<OptimizationRecommendationAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<OptimizationRecommendationAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(OptimizationRecommendationAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(OptimizationRecommendationAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(OptimizationRecommendationAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<OptimizationRecommendationAggregate>> FindAsync(Expression<Func<OptimizationRecommendationAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<OptimizationRecommendationAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(recommendation => recommendation.SiteId == siteId, ct))
                .OrderByDescending(recommendation => recommendation.RaisedUtc)
                .ThenBy(recommendation => recommendation.RecommendationKey, StringComparer.Ordinal)
                .ToList();

        public async Task<OptimizationRecommendationAggregate?> FindForSiteAsync(int siteId, Guid id, CancellationToken ct = default)
        {
            var found = await inner.GetByIdAsync(id, ct);
            return found is not null && found.SiteId == siteId ? found : null;
        }
    }
}
