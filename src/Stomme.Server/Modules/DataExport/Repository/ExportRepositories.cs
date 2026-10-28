#nullable enable
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.DataExport.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.DataExport.Persistence
{
    /// <summary>
    /// The export jobs' closed read surface (D108 item 4).
    /// </summary>
    /// <remarks>
    /// <b>The namespace is deliberate</b> (the Metering and Notifications convention): <c>ModuleStoreRegistrationTests</c>
    /// holds every closed store contract under <c>Stomme.Modules.*</c> to a hand-written EF and RavenDB twin. These
    /// repositories have no twin - they delegate to the storage-blind closed <c>IRepository</c> - and are gated by
    /// <c>ModuleAggregateProviderNeutralityTests</c> instead.
    /// <para>
    /// Every filter stays inside the <c>FindAsync</c> SAFE SET - <c>int</c> ids, a <see cref="Guid"/>, an enum
    /// and UTC <see cref="DateTime"/> ranges. No string appears in a predicate: bare string equality is
    /// case-sensitive on EF and case-insensitive on RavenDB (D49). On the File store every one of these is a full
    /// scan filtered in memory (D108 refutation 15) - acceptable for a per-minute queue on a single-host store,
    /// and stated rather than hidden.
    /// </para>
    /// </remarks>
    public interface IExportJobRepository : IRepository<ExportJobAggregate, Guid>
    {
        /// <summary>
        /// The job with this id IF it belongs to <paramref name="siteId"/>; otherwise null - one answer for "no
        /// such export" and "another site's export", or the id space becomes an oracle.
        /// </summary>
        Task<ExportJobAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<ExportJobAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<ExportJobAggregate>> ListByRequesterAsync(int siteId, int requesterUserId, CancellationToken ct = default);

        Task<IReadOnlyList<ExportJobAggregate>> ListByStatusAsync(int siteId, ExportJobStatus status, CancellationToken ct = default);

        /// <summary>Rendering jobs whose lease ran out at or before <paramref name="nowUtc"/> - candidates to reclaim.</summary>
        Task<IReadOnlyList<ExportJobAggregate>> ListLeaseExpiredAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);

        /// <summary>Ready jobs whose artifact expired at or before <paramref name="nowUtc"/> - the purge's work list.</summary>
        Task<IReadOnlyList<ExportJobAggregate>> ListDueForExpiryAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);
    }

    /// <summary>The export schedules' closed read surface (BL-95) - the same FindAsync safe set as the jobs'.</summary>
    public interface IExportScheduleRepository : IRepository<ExportScheduleAggregate, Guid>
    {
        Task<ExportScheduleAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<ExportScheduleAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        Task<IReadOnlyList<ExportScheduleAggregate>> ListByOwnerAsync(int siteId, int ownerUserId, CancellationToken ct = default);

        /// <summary>Running schedules whose next occurrence is at or before <paramref name="nowUtc"/>.</summary>
        Task<IReadOnlyList<ExportScheduleAggregate>> ListDueAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);
    }

    /// <summary>The audit trail's closed read surface (D108 item 4).</summary>
    public interface IExportAuditRepository : IRepository<ExportAuditEntryAggregate, Guid>
    {
        Task<IReadOnlyList<ExportAuditEntryAggregate>> ListByJobAsync(int siteId, Guid jobId, CancellationToken ct = default);

        Task<IReadOnlyList<ExportAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default);

        /// <summary>Entries older than <paramref name="cutoffUtc"/> - what the retention purge removes.</summary>
        Task<IReadOnlyList<ExportAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);
    }

    /// <summary>
    /// The shared delegation to the closed <see cref="IRepository{TAggregate, TId}"/>: no provider code, no
    /// <c>DbContext</c>, no RavenDB twin. Whatever store the install chose answers these calls (D79/D80).
    /// </summary>
    public abstract class ExportRepositoryBase<TAggregate>(IRepository<TAggregate, Guid> inner)
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

        /// <summary>Refuses a non-UTC instant rather than filtering against a local time the store stored as UTC.</summary>
        protected static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", nameof(value));
    }

    public sealed class ExportJobRepository(IRepository<ExportJobAggregate, Guid> inner)
        : ExportRepositoryBase<ExportJobAggregate>(inner), IExportJobRepository
    {
        public async Task<ExportJobAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default)
        {
            var job = await Inner.GetByIdAsync(id, ct);
            return job is null || job.SiteId != siteId ? null : job;
        }

        public Task<IReadOnlyList<ExportJobAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(job => job.SiteId == siteId, ct);

        public Task<IReadOnlyList<ExportJobAggregate>> ListByRequesterAsync(int siteId, int requesterUserId, CancellationToken ct = default) =>
            Inner.FindAsync(job => job.SiteId == siteId && job.RequesterUserId == requesterUserId, ct);

        public Task<IReadOnlyList<ExportJobAggregate>> ListByStatusAsync(int siteId, ExportJobStatus status, CancellationToken ct = default) =>
            Inner.FindAsync(job => job.SiteId == siteId && job.Status == status, ct);

        public Task<IReadOnlyList<ExportJobAggregate>> ListLeaseExpiredAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            return Inner.FindAsync(
                job => job.SiteId == siteId && job.Status == ExportJobStatus.Rendering && job.LeaseExpiresOnUtc <= now, ct);
        }

        public Task<IReadOnlyList<ExportJobAggregate>> ListDueForExpiryAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            return Inner.FindAsync(
                job => job.SiteId == siteId && job.Status == ExportJobStatus.Ready && job.ExpiresOnUtc <= now, ct);
        }
    }

    public sealed class ExportScheduleRepository(IRepository<ExportScheduleAggregate, Guid> inner)
        : ExportRepositoryBase<ExportScheduleAggregate>(inner), IExportScheduleRepository
    {
        public async Task<ExportScheduleAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default)
        {
            var schedule = await Inner.GetByIdAsync(id, ct);
            return schedule is null || schedule.SiteId != siteId ? null : schedule;
        }

        public Task<IReadOnlyList<ExportScheduleAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(schedule => schedule.SiteId == siteId, ct);

        public Task<IReadOnlyList<ExportScheduleAggregate>> ListByOwnerAsync(int siteId, int ownerUserId, CancellationToken ct = default) =>
            Inner.FindAsync(schedule => schedule.SiteId == siteId && schedule.OwnerUserId == ownerUserId, ct);

        public Task<IReadOnlyList<ExportScheduleAggregate>> ListDueAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            return Inner.FindAsync(schedule => schedule.SiteId == siteId && !schedule.IsPaused && schedule.NextRunOnUtc <= now, ct);
        }
    }

    public sealed class ExportAuditRepository(IRepository<ExportAuditEntryAggregate, Guid> inner)
        : ExportRepositoryBase<ExportAuditEntryAggregate>(inner), IExportAuditRepository
    {
        public Task<IReadOnlyList<ExportAuditEntryAggregate>> ListByJobAsync(int siteId, Guid jobId, CancellationToken ct = default) =>
            Inner.FindAsync(entry => entry.SiteId == siteId && entry.JobId == jobId, ct);

        public Task<IReadOnlyList<ExportAuditEntryAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(entry => entry.SiteId == siteId, ct);

        public Task<IReadOnlyList<ExportAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc < cutoff, ct);
        }
    }
}
