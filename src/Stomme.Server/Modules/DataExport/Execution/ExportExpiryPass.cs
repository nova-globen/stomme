#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Modules.DataExport.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>What one site's expiry pass did.</summary>
    public sealed record ExportExpiryResult(int Expired, int ArtifactsDeleted, int ArtifactsLeftAlone, int AuditPurged, int TempFilesSwept);

    /// <summary>
    /// Retires what has outlived its retention: Ready artifacts, old audit entries, abandoned temp files.
    /// </summary>
    /// <remarks>
    /// <para>
    /// the predecessor platform never purged an export: <c>DeleteAsync</c> had no caller, so every artifact ever rendered stayed on
    /// disk. Here a Ready job past <c>ExpiresOnUtc</c> loses its bytes and its File row and becomes Expired - the job
    /// row stays as history - with an audit entry written at the transition, by this code.
    /// </para>
    /// <para>
    /// <b>The purge deletes ONLY what is still in the Exports folder (refutation 14).</b> Folder Edit lets a file be
    /// moved elsewhere; a File id the job remembers is not licence to delete whatever that row now is. A moved
    /// artifact is LEFT ALONE and logged, and the job still expires - the module stops vouching for it either way.
    /// </para>
    /// <para>
    /// Temp files are swept by AGE, never by name alone: a <c>.tmp</c> younger than the longest a job can legitimately
    /// run belongs to a render that may still be writing it. That age is read off the OS clock, because the OS is
    /// what stamped the file.
    /// </para>
    /// </remarks>
    public sealed class ExportExpiryPass
    {
        private readonly IExportJobRepository _jobs;
        private readonly IExportAuditRepository _audit;
        private readonly IFileRepository _files;
        private readonly IFolderRepository _folders;
        private readonly ExportsFolder _exports;
        private readonly IExportClock _clock;
        private readonly DataExportOptions _options;
        private readonly IExportSiteSettings _siteSettings;
        private readonly ILogger<ExportExpiryPass> _logger;

        public ExportExpiryPass(
            IExportJobRepository jobs,
            IExportAuditRepository audit,
            IFileRepository files,
            IFolderRepository folders,
            ExportsFolder exports,
            IExportClock clock,
            DataExportOptions options,
            IExportSiteSettings siteSettings,
            ILogger<ExportExpiryPass> logger)
        {
            _jobs = jobs;
            _audit = audit;
            _files = files;
            _folders = folders;
            _exports = exports;
            _clock = clock;
            _options = options;
            _siteSettings = siteSettings;
            _logger = logger;
        }

        public async Task<ExportExpiryResult> RunSiteAsync(int siteId, CancellationToken ct = default)
        {
            var now = _clock.UtcNow;
            int expired = 0, deleted = 0, leftAlone = 0;

            foreach (var job in await _jobs.ListDueForExpiryAsync(siteId, now, ct))
            {
                ct.ThrowIfCancellationRequested();

                var removed = await RemoveArtifactAsync(job, ct);
                job.Expire(now);

                try
                {
                    await _jobs.UpdateAsync(job, ct);
                }
                catch (ConcurrencyConflictException)
                {
                    // Another host expired it first. Its bytes are gone either way; nothing more to record.
                    continue;
                }

                await _audit.AddAsync(
                    ExportAuditEntryAggregate.Record(Guid.NewGuid(), job.TenantId, job.SiteId, job.Id, actorUserId: null, ExportAuditAction.Expired, now), ct);

                expired++;
                if (removed == true)
                {
                    deleted++;
                }
                else if (removed == false)
                {
                    leftAlone++;
                }
            }

            var auditPurged = 0;
            foreach (var entry in await _audit.ListOlderThanAsync(siteId, now - await _siteSettings.GetAuditRetentionAsync(siteId, ct), ct))
            {
                ct.ThrowIfCancellationRequested();
                await _audit.DeleteAsync(entry, ct);
                auditPurged++;
            }

            return new ExportExpiryResult(expired, deleted, leftAlone, auditPurged, await SweepTempFilesAsync(siteId));
        }

        /// <returns>True: deleted. False: not ours any more, left alone. Null: there was nothing to delete.</returns>
        private async Task<bool?> RemoveArtifactAsync(ExportJobAggregate job, CancellationToken ct)
        {
            if (job.FileId is not { } fileId)
            {
                return null;
            }

            var file = await _files.GetFileAsync(fileId);
            if (file is null)
            {
                return null;
            }

            if (!await _exports.ContainsAsync(job.SiteId, file))
            {
                _logger.LogWarning(
                    "Export {JobId} expired, but its file {FileId} is no longer in the site's Exports folder. It was NOT deleted.", job.Id, fileId);
                return false;
            }

            var path = await _files.GetFilePathAsync(file);
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }

            await _files.DeleteFileAsync(fileId);
            return true;
        }

        private async Task<int> SweepTempFilesAsync(int siteId)
        {
            var folder = await _exports.FindAsync(siteId);
            if (folder is null)
            {
                return 0;
            }

            var directory = await _folders.GetFolderPathAsync(folder);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return 0;
            }

            // Older than every attempt of the longest job could possibly still be running - measured against the OS
            // clock, NOT the injectable one: a file's modification time is stamped by the OS, and an age is only
            // meaningful against the clock that stamped it. (Driven: with the injected clock six days ahead, the first
            // version of this sweep deleted a temp file written a second earlier.)
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, _options.JobTimeoutMinutes) * (double)ExportJobAggregate.MaximumAttempts) - TimeSpan.FromHours(1);
            var swept = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*" + FolderExportArtifactStore.TempExtension))
            {
                try
                {
                    if (System.IO.File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        System.IO.File.Delete(path);
                        swept++;
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "A stale export temp file could not be removed: {Path}", path);
                }
            }

            return swept;
        }
    }
}
