#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Sessions.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Sessions.Persistence
{
    /// <summary>
    /// The session rows' reads (BL-119). Every filter is an int, a Guid or a UTC range - the provider-safe set (D78). "Active"
    /// is the range filter alone: a revoke moves <see cref="UserSessionAggregate.ExpiresOnUtc"/> to the revocation instant,
    /// so a revoked row is never in the future (pinned by the conformance suite).
    /// </summary>
    public interface IUserSessionRepository : IRepository<UserSessionAggregate, Guid>
    {
        /// <summary>One session by id, or null. A lookup - not tracked (the storage-blind registration never tracks, D79).</summary>
        Task<UserSessionAggregate?> FindSessionAsync(Guid id, CancellationToken ct = default);

        /// <summary>A user's ACTIVE sessions on a site, newest first.</summary>
        Task<IReadOnlyList<UserSessionAggregate>> ListActiveForUserAsync(int siteId, int userId, DateTime nowUtc, CancellationToken ct = default);

        /// <summary>Every ACTIVE session on a site, most recently seen first, at most <paramref name="take"/>.</summary>
        Task<IReadOnlyList<UserSessionAggregate>> ListActiveForSiteAsync(int siteId, DateTime nowUtc, int take, CancellationToken ct = default);

        /// <summary>A site's sessions that ended (expired or revoked) before <paramref name="cutoffUtc"/> - what retention deletes.</summary>
        Task<IReadOnlyList<UserSessionAggregate>> ListEndedBeforeAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);
    }

    /// <summary>The storage-blind implementation over the closed repository (D79/D80) - no provider code.</summary>
    public sealed class UserSessionRepository(IRepository<UserSessionAggregate, Guid> inner) : IUserSessionRepository
    {
        public Task<UserSessionAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<UserSessionAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(UserSessionAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(UserSessionAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(UserSessionAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<UserSessionAggregate>> FindAsync(Expression<Func<UserSessionAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public Task<UserSessionAggregate?> FindSessionAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public async Task<IReadOnlyList<UserSessionAggregate>> ListActiveForUserAsync(int siteId, int userId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            var rows = await inner.FindAsync(session => session.SiteId == siteId && session.UserId == userId && session.ExpiresOnUtc > now, ct);
            return rows.OrderByDescending(session => session.CreatedOnUtc).ToList();
        }

        public async Task<IReadOnlyList<UserSessionAggregate>> ListActiveForSiteAsync(int siteId, DateTime nowUtc, int take, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            var rows = await inner.FindAsync(session => session.SiteId == siteId && session.ExpiresOnUtc > now, ct);
            return rows.OrderByDescending(session => session.LastSeenOnUtc).Take(Math.Max(1, take)).ToList();
        }

        public Task<IReadOnlyList<UserSessionAggregate>> ListEndedBeforeAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return inner.FindAsync(session => session.SiteId == siteId && session.ExpiresOnUtc < cutoff, ct);
        }

        private static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException($"The instant must be UTC (Kind was {value.Kind}).", nameof(value));
    }
}
