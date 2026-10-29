#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Notifications.Persistence
{
    /// <summary>
    /// The shared delegation to the closed <see cref="IRepository{TAggregate, TId}"/> - no provider code, no
    /// <c>DbContext</c>, no RavenDB twin (see <c>INotificationRepositories.cs</c>).
    /// </summary>
    public abstract class NotificationRepositoryBase<TAggregate>(IRepository<TAggregate, Guid> inner)
        : IRepository<TAggregate, Guid>
        where TAggregate : AggregateRoot<Guid>
    {
        protected IRepository<TAggregate, Guid> Inner { get; } = inner;

        public Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => Inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default) => Inner.GetAllAsync(ct);

        public Task AddAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<TAggregate>> FindAsync(Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default) =>
            Inner.FindAsync(predicate, ct);
    }

    public sealed class NotificationRequestRepository(IRepository<NotificationRequestAggregate, Guid> inner)
        : NotificationRepositoryBase<NotificationRequestAggregate>(inner), INotificationRequestRepository
    {
        public Task<IReadOnlyList<NotificationRequestAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(request => request.SiteId == siteId, ct);

        public Task<IReadOnlyList<NotificationRequestAggregate>> ListDueAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            return Inner.FindAsync(request => request.SiteId == siteId
                && request.CompletedOn == null
                && request.DigestDeferredUntil == null
                && request.NextAttemptOn <= now, ct);
        }

        public Task<IReadOnlyList<NotificationRequestAggregate>> ListDigestDueAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            return Inner.FindAsync(request => request.SiteId == siteId
                && request.DigestDeferredUntil != null
                && request.DigestDeferredUntil <= now, ct);
        }

        public Task<IReadOnlyList<NotificationRequestAggregate>> ListPurgeableAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(request => request.SiteId == siteId
                && request.CompletedOn != null
                && request.CompletedOn < cutoff, ct);
        }

        private static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", nameof(value));
    }

    public sealed class NotificationTemplateRepository(IRepository<NotificationTemplateAggregate, Guid> inner)
        : NotificationRepositoryBase<NotificationTemplateAggregate>(inner), INotificationTemplateRepository
    {
        public Task<IReadOnlyList<NotificationTemplateAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(template => template.SiteId == siteId, ct);

        public async Task<NotificationTemplateAggregate?> GetByKeyAsync(int siteId, string key, CancellationToken ct = default)
        {
            // The column is normalized by the aggregate; the argument is normalized HERE with the same function.
            var normalized = NotificationTemplateAggregate.NormalizeKey(key);
            var matches = await Inner.FindAsync(template => template.SiteId == siteId && template.KeyNormalized == normalized, ct);
            return matches.FirstOrDefault();
        }
    }

    public sealed class NotificationChannelRepository(IRepository<NotificationChannelAggregate, Guid> inner)
        : NotificationRepositoryBase<NotificationChannelAggregate>(inner), INotificationChannelRepository
    {
        public Task<IReadOnlyList<NotificationChannelAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(channel => channel.SiteId == siteId, ct);

        public Task<IReadOnlyList<NotificationChannelAggregate>> ListEnabledAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(channel => channel.SiteId == siteId && channel.Enabled == true, ct);

        public async Task<NotificationChannelAggregate?> GetByNameAsync(int siteId, string name, CancellationToken ct = default)
        {
            var normalized = NotificationChannelAggregate.NormalizeName(name);
            var matches = await Inner.FindAsync(channel => channel.SiteId == siteId && channel.NameNormalized == normalized, ct);
            return matches.FirstOrDefault();
        }
    }

    public sealed class NotificationPreferenceRepository(IRepository<NotificationPreferenceAggregate, Guid> inner)
        : NotificationRepositoryBase<NotificationPreferenceAggregate>(inner), INotificationPreferenceRepository
    {
        public Task<IReadOnlyList<NotificationPreferenceAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(preference => preference.SiteId == siteId, ct);

        public async Task<NotificationPreferenceAggregate?> GetByUserAsync(int siteId, int userId, CancellationToken ct = default)
        {
            var matches = await Inner.FindAsync(preference => preference.SiteId == siteId && preference.UserId == userId, ct);
            return matches.FirstOrDefault();
        }
    }
}
