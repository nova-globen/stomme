#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Formats.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Formats.Persistence
{
    /// <summary>
    /// The Formats module's repository - storage-blind: a thin, module-owned contract over the closed
    /// <c>IRepository&lt;FormatDefinitionAggregate, Guid&gt;</c> the host binds to the install's provider. Lives in the
    /// <c>Stomme.Formats.Persistence</c> namespace because <c>ModuleStoreRegistrationTests</c> reads every
    /// <c>Stomme.Modules.*</c> interface closing <c>IRepository</c> as a seam store that needs a RavenDB twin.
    /// </summary>
    public interface IFormatDefinitionRepository : IRepository<FormatDefinitionAggregate, Guid>
    {
        /// <summary>A site's definitions, by normalised name: the same answer on every provider.</summary>
        Task<FormatDefinitionAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default);

        /// <summary>A site's definitions, name order.</summary>
        Task<IReadOnlyList<FormatDefinitionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);
    }

    public sealed class FormatDefinitionRepository(IRepository<FormatDefinitionAggregate, Guid> inner) : IFormatDefinitionRepository
    {
        public Task<FormatDefinitionAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<FormatDefinitionAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(FormatDefinitionAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(FormatDefinitionAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(FormatDefinitionAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<FormatDefinitionAggregate>> FindAsync(Expression<Func<FormatDefinitionAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<FormatDefinitionAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default)
        {
            // Both sides normalised inside the repository: a bare string equality diverges by provider.
            var normalized = FormatDefinitionAggregate.NormalizeName(name);
            var matches = await inner.FindAsync(definition => definition.SiteId == siteId && definition.NameNormalized == normalized, ct);
            return matches.FirstOrDefault();
        }

        public async Task<IReadOnlyList<FormatDefinitionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default)
        {
            var definitions = await inner.FindAsync(definition => definition.SiteId == siteId, ct);
            return definitions.OrderBy(definition => definition.NameNormalized, StringComparer.Ordinal).ToList();
        }
    }
}
