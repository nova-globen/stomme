#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Backup.Persistence
{
    /// <summary>
    /// The snapshot catalogue's reads (BL-81, D106 item 6). Filters are on <c>Status</c> only - an enum, the provider-safe
    /// set (D78); ordering happens in memory, the catalogue being small by construction (retention keeps it so).
    /// </summary>
    public interface IBackupSnapshotRepository : IRepository<BackupSnapshotAggregate, Guid>
    {
        /// <summary>Every snapshot, newest first.</summary>
        Task<IReadOnlyList<BackupSnapshotAggregate>> ListAsync(CancellationToken ct = default);

        /// <summary>The snapshots in one status, oldest first.</summary>
        Task<IReadOnlyList<BackupSnapshotAggregate>> ListByStatusAsync(BackupSnapshotStatus status, CancellationToken ct = default);
    }

    public sealed class BackupSnapshotRepository(IRepository<BackupSnapshotAggregate, Guid> inner) : IBackupSnapshotRepository
    {
        public Task<BackupSnapshotAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<BackupSnapshotAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(BackupSnapshotAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(BackupSnapshotAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(BackupSnapshotAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<BackupSnapshotAggregate>> FindAsync(Expression<Func<BackupSnapshotAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<BackupSnapshotAggregate>> ListAsync(CancellationToken ct = default) =>
            (await inner.GetAllAsync(ct)).OrderByDescending(snapshot => snapshot.CreatedOnUtc).ThenBy(snapshot => snapshot.Id).ToList();

        public async Task<IReadOnlyList<BackupSnapshotAggregate>> ListByStatusAsync(BackupSnapshotStatus status, CancellationToken ct = default) =>
            (await inner.FindAsync(snapshot => snapshot.Status == status, ct)).OrderBy(snapshot => snapshot.CreatedOnUtc).ThenBy(snapshot => snapshot.Id).ToList();
    }

    /// <summary>The backup schedules (D106 item 8).</summary>
    public interface IBackupScheduleRepository : IRepository<BackupScheduleAggregate, Guid>
    {
        /// <summary>Every schedule, oldest first.</summary>
        Task<IReadOnlyList<BackupScheduleAggregate>> ListAsync(CancellationToken ct = default);
    }

    public sealed class BackupScheduleRepository(IRepository<BackupScheduleAggregate, Guid> inner) : IBackupScheduleRepository
    {
        public Task<BackupScheduleAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<BackupScheduleAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(BackupScheduleAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(BackupScheduleAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(BackupScheduleAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<BackupScheduleAggregate>> FindAsync(Expression<Func<BackupScheduleAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<BackupScheduleAggregate>> ListAsync(CancellationToken ct = default) =>
            (await inner.GetAllAsync(ct)).OrderBy(schedule => schedule.CreatedOnUtc).ThenBy(schedule => schedule.Id).ToList();
    }
}
