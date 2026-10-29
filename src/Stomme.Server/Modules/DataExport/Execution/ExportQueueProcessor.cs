#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Persistence.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>The clock, so a test can run a lease out without waiting for it.</summary>
    public interface IExportClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemExportClock : IExportClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    /// <summary>The knobs of <c>DataExport</c> in configuration, with their defaults.</summary>
    public sealed class DataExportOptions
    {
        public const string SectionName = "DataExport";

        public bool Enabled { get; set; }

        public long MaxArtifactBytes { get; set; } = 268_435_456;

        public int JobTimeoutMinutes { get; set; } = 60;

        public int LeaseMinutes { get; set; } = 10;

        public int HeartbeatRows { get; set; } = 10_000;

        public int FolderCapacityMb { get; set; } = 1024;

        public int RetentionDays { get; set; } = 7;

        public int AuditRetentionDays { get; set; } = 90;

        public TimeSpan Lease => TimeSpan.FromMinutes(Math.Max(1, LeaseMinutes));

        public TimeSpan Retention => TimeSpan.FromDays(Math.Max(1, RetentionDays));

        /// <summary>Never shorter than the artifact retention: an export's trail must outlive the export.</summary>
        public TimeSpan AuditRetention => TimeSpan.FromDays(Math.Max(Math.Max(1, RetentionDays), AuditRetentionDays));

        /// <summary><c>File.Size</c> is an <c>int</c>, so no artifact may exceed it whatever the configuration says.</summary>
        public long EffectiveMaxArtifactBytes => Math.Clamp(MaxArtifactBytes, 1, int.MaxValue);
    }

    /// <summary>
    /// Decides, from STORED rows, whether the requester may still run this export (D108 refutation 8).
    /// </summary>
    /// <remarks>
    /// A job has no <c>HttpContext</c> and no claims, and the requester's roles may have changed since the
    /// request. The implementation is a later increment; the seam is here so the processor cannot be written
    /// without calling it.
    /// </remarks>
    public interface IExportJobAuthorizer
    {
        Task<bool> IsAuthorizedAsync(ExportJobAggregate job, IExportSource source, CancellationToken ct = default);
    }

    /// <summary>
    /// Where an attempt's bytes go, and how they become the artifact.
    /// </summary>
    /// <remarks>
    /// Module-internal on purpose: artifacts are framework File rows in the site's private <c>Exports</c> folder,
    /// not a second blob port. Each ATTEMPT gets its own destination (refutation 9) - two attempts of one job
    /// must never share a temp file, or a reclaimed render and the one that lost its lease race on the same bytes.
    /// </remarks>
    public interface IExportArtifactStore
    {
        /// <summary>A fresh, empty destination for this attempt of this job.</summary>
        Task<Stream> OpenAttemptAsync(ExportJobAggregate job, CancellationToken ct = default);

        /// <summary>Turns the attempt's bytes into the artifact and returns its File id.</summary>
        Task<int> CommitAttemptAsync(ExportJobAggregate job, long sizeBytes, CancellationToken ct = default);

        /// <summary>Removes the attempt's bytes. Must not throw when there are none.</summary>
        Task DiscardAttemptAsync(ExportJobAggregate job, CancellationToken ct = default);
    }

    /// <summary>What one drain of one site did.</summary>
    public sealed record ExportDrainResult(int Completed, int Failed, int Abandoned, int LostRace);

    /// <summary>
    /// Claims and renders a site's queued exports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Exactly-once is the store's concurrency check, nothing else.</b> No lock and no semaphore: the host
    /// registers no <c>IDistributedLock</c> on any provider (refutation 2). A drain loads a Queued job, calls
    /// <see cref="ExportJobAggregate.Claim"/> and saves; of two drains doing that at once, the store accepts one
    /// write and gives the other a <see cref="ConcurrencyConflictException"/>, which here means "someone else
    /// has it" and is not an error. The File store has no such check, which is sound only because it is
    /// single-host - asserted, not assumed, in <c>ExportQueueLeaseTests</c>.
    /// </para>
    /// <para>
    /// <b>Every write of a job goes through THE INSTANCE THIS SCOPE LOADED.</b> That is what RavenDB's
    /// change-vector check and EF's version token both require, and it is the shape
    /// <c>PerWriteStoreInvariantTests</c> calls safe: no mapper-fresh instance with a key that was also read.
    /// </para>
    /// <para>
    /// Scoped, and resolved inside the site's own DI scope by the hosted job - the <c>UsageRollupPass</c> shape,
    /// so tests drive it without a hosted service.
    /// </para>
    /// </remarks>
    public sealed class ExportQueueProcessor
    {
        private readonly IExportJobRepository _jobs;
        private readonly IExportAuditRepository _audit;
        private readonly ExportSourceRegistry _sources;
        private readonly ExportRendererRegistry _renderers;
        private readonly IExportArtifactStore _artifacts;
        private readonly IExportJobAuthorizer _authorizer;
        private readonly IExportCellFormatter _formatter;
        private readonly IServiceScopeFactory _scopes;
        private readonly IExportClock _clock;
        private readonly ExportReadyNotifier _ready;
        private readonly DataExportOptions _options;
        private readonly IExportSiteSettings _siteSettings;
        private readonly ILogger<ExportQueueProcessor> _logger;

        public ExportQueueProcessor(
            IExportJobRepository jobs,
            IExportAuditRepository audit,
            ExportSourceRegistry sources,
            ExportRendererRegistry renderers,
            IExportArtifactStore artifacts,
            IExportJobAuthorizer authorizer,
            IExportCellFormatter formatter,
            IServiceScopeFactory scopes,
            IExportClock clock,
            ExportReadyNotifier ready,
            DataExportOptions options,
            IExportSiteSettings siteSettings,
            ILogger<ExportQueueProcessor> logger)
        {
            _jobs = jobs;
            _audit = audit;
            _sources = sources;
            _renderers = renderers;
            _artifacts = artifacts;
            _authorizer = authorizer;
            _formatter = formatter;
            _scopes = scopes;
            _clock = clock;
            _ready = ready;
            _options = options;
            _siteSettings = siteSettings;
            _logger = logger;
        }

        public async Task<ExportDrainResult> DrainSiteAsync(int siteId, CancellationToken ct = default)
        {
            int completed = 0, failed = 0, abandoned = 0, lostRace = 0;

            void Count(ExportAttemptOutcome outcome)
            {
                switch (outcome)
                {
                    case ExportAttemptOutcome.Completed: completed++; break;
                    case ExportAttemptOutcome.Failed: failed++; break;
                    case ExportAttemptOutcome.Abandoned: abandoned++; break;
                    case ExportAttemptOutcome.LostRace: lostRace++; break;
                }
            }

            // Dead leases first: a job that has been waiting since its renderer died is older than anything queued.
            foreach (var job in await _jobs.ListLeaseExpiredAsync(siteId, _clock.UtcNow, ct))
            {
                ct.ThrowIfCancellationRequested();
                Count(await TakeOverAsync(job, ct));
            }

            var queued = (await _jobs.ListByStatusAsync(siteId, ExportJobStatus.Queued, ct))
                .OrderBy(job => job.RequestedOnUtc)
                .ToList();

            foreach (var job in queued)
            {
                ct.ThrowIfCancellationRequested();
                Count(await ClaimAsync(job, ct));
            }

            return new ExportDrainResult(completed, failed, abandoned, lostRace);
        }

        private async Task<ExportAttemptOutcome> ClaimAsync(ExportJobAggregate job, CancellationToken ct)
        {
            job.Claim(_clock.UtcNow, _options.Lease);
            if (!await TrySaveAsync(job, ct))
            {
                return ExportAttemptOutcome.LostRace;
            }

            return await RenderAsync(job, ct);
        }

        private async Task<ExportAttemptOutcome> TakeOverAsync(ExportJobAggregate job, CancellationToken ct)
        {
            var now = _clock.UtcNow;
            if (!job.IsLeaseExpired(now))
            {
                return ExportAttemptOutcome.LostRace;
            }

            var reclaimed = job.Reclaim(now, _options.Lease);
            if (!await TrySaveAsync(job, ct))
            {
                return ExportAttemptOutcome.LostRace;
            }

            if (!reclaimed)
            {
                // The attempts are spent: Reclaim already moved the job to Failed, and that is what was saved.
                await RecordAsync(job, ExportAuditAction.Failed, ct, ExportFailureCode.AttemptsExhausted);
                return ExportAttemptOutcome.Failed;
            }

            return await RenderAsync(job, ct);
        }

        private async Task<ExportAttemptOutcome> RenderAsync(ExportJobAggregate job, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.JobTimeoutMinutes)));

            try
            {
                if (!_sources.TryGet(job.SourceKey, out var source))
                {
                    return await FailAsync(job, ExportFailureCode.SourceUnavailable, ct);
                }

                if (!_renderers.TryGet(job.Format, out var renderer))
                {
                    return await FailAsync(job, ExportFailureCode.RenderError, ct);
                }

                // Re-authorised NOW, from stored rows: the requester's access may have been revoked since the request.
                if (!await _authorizer.IsAuthorizedAsync(job, source, timeout.Token))
                {
                    return await FailAsync(job, ExportFailureCode.NotAuthorized, ct);
                }

                var context = new ExportSourceContext(job.SiteId, job.RequesterUserId, job.ArgumentsJson);
                if (source.Validate(context) is not null)
                {
                    return await FailAsync(job, ExportFailureCode.InvalidArguments, ct);
                }

                RenderOutcome outcome;
                long size;

                if (_formatter is IExportCellFormatterPreparation preparation)
                {
                    await preparation.PrepareAsync(job.SiteId, timeout.Token); // Formats' per-site definitions, once per export (D110 increment 8)
                }

                await using (var document = await source.OpenAsync(context, timeout.Token))
                await using (var file = await _artifacts.OpenAttemptAsync(job, timeout.Token))
                {
                    var destination = new CappedCountingStream(file, _options.EffectiveMaxArtifactBytes);
                    var pulsing = new PulsingDocument(document, Math.Max(1, _options.HeartbeatRows), token => PulseAsync(job, token));

                    outcome = await renderer.RenderAsync(pulsing, destination, ExportOptionsJson.Parse(job.OptionsJson), _formatter, timeout.Token);
                    await destination.FlushAsync(timeout.Token);
                    size = destination.BytesWritten;
                }

                // One last look before the artifact becomes real: a cancel that arrived during the final rows.
                await PulseAsync(job, timeout.Token);

                var fileId = await _artifacts.CommitAttemptAsync(job, size, timeout.Token);
                job.Complete(fileId, outcome.RowCount, size, _clock.UtcNow, await _siteSettings.GetRetentionAsync(job.SiteId, ct));
                if (!await TrySaveAsync(job, ct))
                {
                    // The job changed under us between the pulse and the save. The artifact is orphaned by design
                    // rather than attached to a job someone else now owns; the purge removes it.
                    return ExportAttemptOutcome.Abandoned;
                }

                await RecordAsync(job, ExportAuditAction.Completed, ct);

                // AFTER the Ready transition is saved and audited, and best-effort: see ExportReadyNotifier.
                await _ready.NotifyAsync(job, ct);
                return ExportAttemptOutcome.Completed;
            }
            catch (ExportRenderAbandonedException abandoned)
            {
                _logger.LogInformation("Export {JobId} stopped rendering: {Reason}", job.Id, abandoned.Message);
                await DiscardQuietlyAsync(job);
                return ExportAttemptOutcome.Abandoned;
            }
            catch (ExportArtifactTooLargeException)
            {
                await DiscardQuietlyAsync(job);
                return await FailAsync(job, ExportFailureCode.ArtifactTooLarge, ct);
            }
            catch (ExportRenderException ex)
            {
                _logger.LogWarning(ex, "Export {JobId} could not be rendered.", job.Id);
                await DiscardQuietlyAsync(job);
                return await FailAsync(job, ExportFailureCode.RenderError, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await DiscardQuietlyAsync(job);
                return await FailAsync(job, ExportFailureCode.TimedOut, ct);
            }
            catch (OperationCanceledException)
            {
                // The host is stopping. The lease simply runs out and another drain takes the job over.
                await DiscardQuietlyAsync(job);
                throw;
            }
            catch (Exception ex)
            {
                // The TEXT goes to the log. The job carries a code and nothing else (see ExportFailureCode).
                _logger.LogError(ex, "Export {JobId} failed.", job.Id);
                await DiscardQuietlyAsync(job);
                return await FailAsync(job, ExportFailureCode.Unknown, ct);
            }
        }

        /// <summary>
        /// Between tables and every N rows: is this still our job, and if so renew the lease.
        /// </summary>
        private async Task PulseAsync(ExportJobAggregate job, CancellationToken ct)
        {
            // A FRESH scope: a read through this scope's repository would hand back the instance already being
            // tracked here and could never see a cancel written by someone else. This is the only signal the File
            // store gives; on EF and RavenDB the heartbeat's own concurrency check below catches it as well.
            ExportJobStatus? status;
            int attempts;
            await using (var scope = _scopes.CreateAsyncScope())
            {
                var current = await scope.ServiceProvider.GetRequiredService<IExportJobRepository>().GetByIdAsync(job.Id, ct);
                status = current?.Status;
                attempts = current?.Attempts ?? 0;
            }

            if (status != ExportJobStatus.Rendering)
            {
                throw new ExportRenderAbandonedException($"the job is {status?.ToString() ?? "gone"}");
            }

            if (attempts != job.Attempts)
            {
                throw new ExportRenderAbandonedException("the lease was taken over by another attempt");
            }

            job.Heartbeat(_clock.UtcNow, _options.Lease);
            if (!await TrySaveAsync(job, ct))
            {
                throw new ExportRenderAbandonedException("the heartbeat lost the concurrency check");
            }
        }

        private async Task<ExportAttemptOutcome> FailAsync(ExportJobAggregate job, ExportFailureCode code, CancellationToken ct)
        {
            job.Fail(code, _clock.UtcNow);
            if (!await TrySaveAsync(job, ct))
            {
                return ExportAttemptOutcome.Abandoned;
            }

            await RecordAsync(job, ExportAuditAction.Failed, ct, code);
            return ExportAttemptOutcome.Failed;
        }

        /// <summary>Saves the loaded instance; false means another writer got there first.</summary>
        private async Task<bool> TrySaveAsync(ExportJobAggregate job, CancellationToken ct)
        {
            try
            {
                await _jobs.UpdateAsync(job, ct);
                return true;
            }
            catch (ConcurrencyConflictException)
            {
                return false;
            }
        }

        // Written HERE, at the transition, by application code - never by a domain-event handler.
        private Task RecordAsync(ExportJobAggregate job, ExportAuditAction action, CancellationToken ct, ExportFailureCode? code = null) =>
            _audit.AddAsync(
                ExportAuditEntryAggregate.Record(Guid.NewGuid(), job.TenantId, job.SiteId, job.Id, actorUserId: null, action, _clock.UtcNow, code),
                ct);

        private async Task DiscardQuietlyAsync(ExportJobAggregate job)
        {
            try
            {
                await _artifacts.DiscardAttemptAsync(job, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The temporary artifact of export {JobId} could not be removed.", job.Id);
            }
        }

        private enum ExportAttemptOutcome
        {
            Completed,
            Failed,
            Abandoned,
            LostRace,
        }
    }
}
