#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Application.Archive;
using Stomme.Modules.Backup.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Backup.Services
{
    /// <summary>A request the server refuses, with a reason written to be shown to the Host.</summary>
    public sealed class BackupRequestRejectedException(string message) : Exception(message);

    /// <summary>The last word on each queued restore - held in memory, so the page can show what the worker decided.</summary>
    public sealed class BackupOperationLog
    {
        private readonly ConcurrentDictionary<Guid, BackupOperationResult> _results = new();

        public void Record(Guid snapshotId, BackupOperationResult result) => _results[snapshotId] = result;

        public BackupOperationResult? Find(Guid snapshotId) => _results.TryGetValue(snapshotId, out var result) ? result : null;
    }

    /// <summary>
    /// The SERVER side of the backup surface. Everything is the HOST's: every call re-asserts the Host role
    /// (refutation 8 - no permission key exists, because <c>PermissionKeys.Declare</c> always adds Administrators, and an archive
    /// holds every site's users and password hashes under a passphrase its creator chooses). Long work - a snapshot, staging a
    /// restore - is queued to the worker with the passphrase held only in memory; never run inside the request.
    /// </summary>
    public sealed class ServerBackupService(
        BackupEngine engine,
        BackupWorkQueue queue,
        BackupOperationLog operations,
        BackupOptions options,
        ICurrentUser currentUser) : IBackupService
    {
        public Task<bool> IsEnabledAsync() => Task.FromResult(options.Enabled);

        public async Task<List<BackupSnapshotView>> ListAsync()
        {
            RequireHost();
            return (await engine.ListSnapshotsAsync()).Select(ToView).ToList();
        }

        public async Task<BackupOperationResult> CreateAsync(BackupPassphraseRequest request)
        {
            RequireHost();
            if (BackupEngine.PassphraseProblem(request?.Passphrase) is { } problem)
            {
                throw new BackupRequestRejectedException(problem);
            }

            var passphrase = request!.Passphrase;
            var id = await engine.StartSnapshotAsync(RetentionTag.Manual);
            if (!queue.TryEnqueue((provider, token) => provider.GetRequiredService<BackupEngine>().TakeSnapshotAsync(id, passphrase, token)))
            {
                await engine.FailSnapshotAsync(id, "queue full");
                throw new BackupRequestRejectedException("The backup queue is full; try again when the current work has finished.");
            }

            return new BackupOperationResult { Accepted = true, SnapshotId = id };
        }

        public async Task<BackupOperationResult> RestoreAsync(Guid snapshotId, BackupPassphraseRequest request)
        {
            RequireHost();
            if (BackupEngine.PassphraseProblem(request?.Passphrase) is { } problem)
            {
                throw new BackupRequestRejectedException(problem);
            }

            if (await engine.GetSnapshotAsync(snapshotId) is not { } snapshot || !(snapshot.Status is BackupSnapshotStatus.Available or BackupSnapshotStatus.RestoreFailed))
            {
                throw new BackupRequestRejectedException("There is no restorable snapshot with that id.");
            }

            var passphrase = request!.Passphrase;
            var tenantIds = request.TenantIds is { Count: > 0 } ? request.TenantIds.ToList() : null; // Null is the whole installation
            operations.Record(snapshotId, new BackupOperationResult { Accepted = true, SnapshotId = snapshotId, Completed = false });
            var queued = queue.TryEnqueue(async (provider, token) =>
            {
                var report = await provider.GetRequiredService<BackupEngine>().StageRestoreAsync(snapshotId, passphrase, tenantIds, token);
                operations.Record(snapshotId, new BackupOperationResult { Accepted = report.Ok, SnapshotId = snapshotId, Problems = report.Problems.ToList(), Completed = true });
            });
            if (!queued)
            {
                throw new BackupRequestRejectedException("The backup queue is full; try again when the current work has finished.");
            }

            return new BackupOperationResult { Accepted = true, SnapshotId = snapshotId };
        }

        public Task<BackupOperationResult?> RestoreReportAsync(Guid snapshotId)
        {
            RequireHost();
            return Task.FromResult(operations.Find(snapshotId));
        }

        public async Task DeleteAsync(Guid snapshotId)
        {
            RequireHost();
            try
            {
                await engine.DeleteSnapshotAsync(snapshotId);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
            {
                throw new BackupRequestRejectedException(ex.Message);
            }
        }

        public async Task<BackupOperationResult> ImportAsync(Stream archive, string passphrase)
        {
            RequireHost();
            try
            {
                var id = await engine.ImportArchiveAsync(archive, passphrase);
                return new BackupOperationResult { Accepted = true, SnapshotId = id, Completed = true };
            }
            catch (Exception ex) when (ex is BackupArchiveException or ArgumentException)
            {
                throw new BackupRequestRejectedException(ex.Message);
            }
        }

        public async Task<List<BackupScheduleView>> SchedulesAsync()
        {
            RequireHost();
            return (await engine.ListSchedulesAsync()).Select(ToView).ToList();
        }

        public async Task<BackupScheduleView> SaveScheduleAsync(BackupScheduleView view)
        {
            RequireHost();
            ArgumentNullException.ThrowIfNull(view);
            var existing = (await engine.ListSchedulesAsync()).FirstOrDefault(schedule => schedule.Id == view.Id);
            try
            {
                if (existing is null)
                {
                    existing = BackupScheduleAggregate.Create(view.IntervalHours, view.RetentionDays, view.KeepLast, view.Enabled, DateTime.UtcNow);
                }
                else
                {
                    existing.Redefine(view.IntervalHours, view.RetentionDays, view.KeepLast, view.Enabled);
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new BackupRequestRejectedException(ex.Message);
            }

            await engine.SaveScheduleAsync(existing);
            return ToView(existing);
        }

        /// <summary>Opens a snapshot's archive for download - the controller streams it.</summary>
        public Task<Stream?> OpenArchiveAsync(Guid snapshotId)
        {
            RequireHost();
            return engine.OpenArchiveAsync(snapshotId);
        }

        private void RequireHost()
        {
            if (!currentUser.IsAuthenticated || !currentUser.Roles.Contains(RoleNames.Host))
            {
                throw new UnauthorizedAccessException("Backup is the Host's.");
            }
        }

        internal static BackupSnapshotView ToView(BackupSnapshotAggregate row) => new()
        {
            Id = row.Id, Status = row.Status.ToString(), RetentionTag = row.RetentionTag.Value, FrameworkVersion = row.FrameworkVersion,
            SizeBytes = row.SizeBytes, CreatedOnUtc = row.CreatedOnUtc, CompletedOnUtc = row.CompletedOnUtc, StagedOnUtc = row.StagedOnUtc,
            LastRestoredOnUtc = row.LastRestoredOnUtc, FailureReason = row.FailureReason,
            CanRestore = row.Status is BackupSnapshotStatus.Available or BackupSnapshotStatus.RestoreFailed, CanDelete = row.CanBeDeleted(),
        };

        private static BackupScheduleView ToView(BackupScheduleAggregate row) => new()
        {
            Id = row.Id, Enabled = row.Enabled, IntervalHours = row.IntervalHours, RetentionDays = row.RetentionDays, KeepLast = row.KeepLast, LastRunOnUtc = row.LastRunOnUtc,
        };
    }
}
