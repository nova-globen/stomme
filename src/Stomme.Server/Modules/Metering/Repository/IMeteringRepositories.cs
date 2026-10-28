#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Metering.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Metering.Persistence
{
    /// <summary>
    /// The module's closed read surface (D102). Every filter uses only the <c>FindAsync</c> SAFE SET - <c>int</c>
    /// site ids, <c>long</c> quantities, enums, nullable <c>int</c> user ids, UTC <see cref="DateTime"/> ranges, and
    /// NORMALIZED strings compared on both sides - because bare string equality is case-sensitive on EF and
    /// case-insensitive on RavenDB (D49). <see cref="decimal"/> appears in no predicate anywhere: it round-trips,
    /// but it sits outside the contract's safe set.
    /// </summary>
    /// <remarks>
    /// <b><see cref="IQuotaRepository.GetForCurrentSiteAsync"/> is the by-id rule, not a convention.</b> Every
    /// by-id handler (edit, redefine, reset, override, suspend) goes through it, so a caller naming another site's
    /// quota gets the SAME not-found answer as one naming a quota that does not exist - a by-id read that only
    /// checked existence would let one site probe another's configuration by id (the §J.BL-80 item 4 precedent).
    /// </remarks>
    public interface IQuotaRepository : IRepository<QuotaAggregate, Guid>
    {
        Task<IReadOnlyList<QuotaAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>The quota with this id IF it belongs to <paramref name="siteId"/>; otherwise null - one answer for both refusals.</summary>
        Task<QuotaAggregate?> GetForCurrentSiteAsync(Guid id, int siteId, CancellationToken ct = default);

        /// <summary>The quota for one metric and window on a site, addressed by its deterministic id.</summary>
        Task<QuotaAggregate?> GetAsync(TenantId tenantId, int siteId, string metric, QuotaPeriod period, CancellationToken ct = default);
    }

    public interface IUsageRecordRepository : IRepository<UsageRecordAggregate, Guid>
    {
        Task<IReadOnlyList<UsageRecordAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>Rows appended but never folded, older than <paramref name="olderThanUtc"/> - the recovery job's work list.</summary>
        Task<IReadOnlyList<UsageRecordAggregate>> ListPendingAsync(int siteId, DateTime olderThanUtc, CancellationToken ct = default);

        /// <summary>The rows of one metric inside a half-open UTC window - what the repair pass re-counts.</summary>
        Task<IReadOnlyList<UsageRecordAggregate>> ListForWindowAsync(int siteId, string metric, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

        Task<IReadOnlyList<UsageRecordAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default);

        Task<IReadOnlyList<UsageRecordAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);

        Task<IReadOnlyList<UsageRecordAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default);
    }

    public interface IAiUsageRecordRepository : IRepository<AiUsageRecordAggregate, Guid>
    {
        Task<IReadOnlyList<AiUsageRecordAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<AiUsageRecordAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default);

        Task<IReadOnlyList<AiUsageRecordAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);

        Task<IReadOnlyList<AiUsageRecordAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default);
    }

    public interface IAiUsageRollupRepository : IRepository<AiUsageRollupAggregate, Guid>
    {
        Task<IReadOnlyList<AiUsageRollupAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<AiUsageRollupAggregate>> ListSinceAsync(int siteId, DateTime fromDayUtc, CancellationToken ct = default);

        Task<IReadOnlyList<AiUsageRollupAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffDayUtc, CancellationToken ct = default);
    }
}
