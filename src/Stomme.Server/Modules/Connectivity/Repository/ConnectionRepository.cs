#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Connectivity.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Connectivity.Persistence
{
    /// <summary>The Connectivity module's repository - storage-blind over the closed <c>IRepository&lt;ConnectionAggregate, Guid&gt;</c>; in the <c>Persistence</c> namespace the store-registration gate expects of a clean-arch module.</summary>
    public interface IConnectionRepository : IRepository<ConnectionAggregate, Guid>
    {
        /// <summary>A site's connection by normalised name: the same answer on every provider.</summary>
        Task<ConnectionAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default);

        /// <summary>A site's connections, name order.</summary>
        Task<IReadOnlyList<ConnectionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);
    }

    public sealed class ConnectionRepository(IRepository<ConnectionAggregate, Guid> inner) : IConnectionRepository
    {
        public Task<ConnectionAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<ConnectionAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(ConnectionAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(ConnectionAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(ConnectionAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<ConnectionAggregate>> FindAsync(Expression<Func<ConnectionAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<ConnectionAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default)
        {
            var normalized = ConnectionAggregate.NormalizeName(name);
            return (await inner.FindAsync(connection => connection.SiteId == siteId && connection.NameNormalized == normalized, ct)).FirstOrDefault();
        }

        public async Task<IReadOnlyList<ConnectionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(connection => connection.SiteId == siteId, ct)).OrderBy(connection => connection.NameNormalized, StringComparer.Ordinal).ToList();
    }
}
