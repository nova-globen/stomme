#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Notifications.Persistence
{
    // The Notifications module's CLOSED repositories. They live in Stomme.Notifications.Persistence, DELIBERATELY
    // OUTSIDE Stomme.Modules.* - exactly as Guestbook's IGuestbookRepository does - because ModuleStoreRegistrationTests
    // discovers every interface under Stomme.Modules.* that closes IRepository<,> and demands a hand-written RavenDB
    // twin (the seam parity gate). These ride the storage-blind clean-arch stack: one implementation per
    // contract, delegating to the CLOSED IRepository<TAggregate, Guid> AddModuleAggregatePersistence registered, so
    // they run unchanged on EF, RavenDB and File. Every filter is a FindAsync on a non-string scalar or on a
    // normalized column with BOTH sides normalized (FindAsync's contract: strings diverge between providers).

    /// <summary>Persistence for <see cref="NotificationRequestAggregate"/>.</summary>
    public interface INotificationRequestRepository : IRepository<NotificationRequestAggregate, Guid>
    {
        /// <summary>A site's requests (the send log).</summary>
        Task<IReadOnlyList<NotificationRequestAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>
        /// The dispatch job's due work for a site: <c>SiteId == siteId &amp;&amp; CompletedOn == null &amp;&amp;
        /// DigestDeferredUntil == null &amp;&amp; NextAttemptOn &lt;= nowUtc</c>. It does not select on
        /// <c>Status == Queued</c>: <c>Status</c> is derived from the attempts, so a request whose first pass
        /// failed is <c>Failed</c> and its retry would never be selected; <c>CompletedOn</c> is the scalar in-flight marker,
        /// and a digest hold is the hourly flush's, not this pass's.
        /// </summary>
        Task<IReadOnlyList<NotificationRequestAggregate>> ListDueAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);

        /// <summary>The hourly digest flush for a site: <c>SiteId == siteId &amp;&amp; DigestDeferredUntil != null &amp;&amp; DigestDeferredUntil &lt;= nowUtc</c>.</summary>
        Task<IReadOnlyList<NotificationRequestAggregate>> ListDigestDueAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);

        /// <summary>The retention purge for a site: <c>SiteId == siteId &amp;&amp; CompletedOn != null &amp;&amp; CompletedOn &lt; cutoffUtc</c>.</summary>
        Task<IReadOnlyList<NotificationRequestAggregate>> ListPurgeableAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);
    }

    /// <summary>Persistence for <see cref="NotificationTemplateAggregate"/>.</summary>
    public interface INotificationTemplateRepository : IRepository<NotificationTemplateAggregate, Guid>
    {
        /// <summary>A site's templates.</summary>
        Task<IReadOnlyList<NotificationTemplateAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>A site's template with the given key (case-insensitive, via <c>KeyNormalized</c>), or null.</summary>
        Task<NotificationTemplateAggregate?> GetByKeyAsync(int siteId, string key, CancellationToken ct = default);
    }

    /// <summary>Persistence for <see cref="NotificationChannelAggregate"/>.</summary>
    public interface INotificationChannelRepository : IRepository<NotificationChannelAggregate, Guid>
    {
        /// <summary>A site's channels.</summary>
        Task<IReadOnlyList<NotificationChannelAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>A site's ENABLED channels - the candidates a dispatch fans out over.</summary>
        Task<IReadOnlyList<NotificationChannelAggregate>> ListEnabledAsync(int siteId, CancellationToken ct = default);

        /// <summary>A site's channel with the given name (case-insensitive, via <c>NameNormalized</c>), or null.</summary>
        Task<NotificationChannelAggregate?> GetByNameAsync(int siteId, string name, CancellationToken ct = default);
    }

    /// <summary>Persistence for <see cref="NotificationPreferenceAggregate"/> (one per site user).</summary>
    public interface INotificationPreferenceRepository : IRepository<NotificationPreferenceAggregate, Guid>
    {
        /// <summary>A site's preference records.</summary>
        Task<IReadOnlyList<NotificationPreferenceAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>The site user's preferences, or null when none are set (= accept everything).</summary>
        Task<NotificationPreferenceAggregate?> GetByUserAsync(int siteId, int userId, CancellationToken ct = default);
    }
}
