#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.DataExport.Persistence;
using Stomme.Export;
using Stomme.Mediator;
using Stomme.Models.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.DataExport.Execution;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Modules.DataExport.Application
{
    /// <summary>A request the server refuses, with a reason that is safe to show the requester.</summary>
    public sealed class ExportRequestRejectedException : Exception
    {
        public ExportRequestRejectedException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Who is asking, and on which site - resolved from the REQUEST, never from anything the client sent.
    /// </summary>
    public sealed class ExportCaller
    {
        private readonly ICurrentUser _currentUser;
        private readonly ITenantContext _tenantContext;
        private readonly SiteState _siteState;
        private readonly IAccessGuard _guard;

        public ExportCaller(ICurrentUser currentUser, ITenantContext tenantContext, SiteState siteState, IAccessGuard guard)
        {
            _currentUser = currentUser;
            _tenantContext = tenantContext;
            _siteState = siteState;
            _guard = guard;
        }

        public int SiteId
        {
            get
            {
                var siteId = _siteState.Alias?.SiteId ?? -1;
                return siteId > 0 ? siteId : throw new InvalidOperationException("No site is resolved in this scope.");
            }
        }

        public int UserId =>
            _currentUser.IsAuthenticated && _currentUser.UserId is > 0
                ? _currentUser.UserId.Value
                : throw new InvalidOperationException("No authenticated user is resolved in this scope.");

        public Tenancy.Domain.TenantId TenantId =>
            _tenantContext.CurrentTenantId ?? throw new InvalidOperationException("No tenant is resolved in this scope.");

        public Task<bool> HoldsAsync(string permissionKey, CancellationToken ct) => _guard.IsAllowedAsync(null, permissionKey, ct);

        /// <summary>
        /// The job with this id IF the caller may see it: their own, or any on the site for a manage holder.
        /// </summary>
        /// <remarks>
        /// ONE answer - null - for "no such export", "another site's export" and "someone else's export". Anything
        /// more specific turns the id space into an oracle for which exports exist.
        /// </remarks>
        public async Task<ExportJobAggregate?> FindVisibleAsync(IExportJobRepository jobs, Guid id, CancellationToken ct)
        {
            var job = await jobs.GetForSiteAsync(id, SiteId, ct);
            if (job is null)
            {
                return null;
            }

            return job.RequesterUserId == UserId || await HoldsAsync(ExportPermissions.Manage, ct) ? job : null;
        }
    }

    internal static class ExportWire
    {
        public static ExportJobView ToView(ExportJobAggregate job, bool canDownload) => new()
        {
            Id = job.Id,
            RequesterUserId = job.RequesterUserId,
            SourceKey = job.SourceKey,
            Format = job.Format.ToString(),
            Status = job.Status.ToString(),
            FailureCode = job.FailureCode?.ToString(),
            RequestedOnUtc = job.RequestedOnUtc,
            CompletedOnUtc = job.CompletedOnUtc,
            ExpiresOnUtc = job.ExpiresOnUtc,
            RowCount = job.RowCount,
            SizeBytes = job.SizeBytes,
            CanDownload = canDownload,
        };

        public static List<ExportJobView> ToViews(IEnumerable<ExportJobAggregate> jobs, DateTime nowUtc) =>
            jobs.OrderByDescending(job => job.RequestedOnUtc)
                // The LIST answers from the row alone; the download itself re-asserts where the file is (refutation 14).
                .Select(job => ToView(job, job.IsDownloadable(nowUtc)))
                .ToList();
    }

    // ---- request ---------------------------------------------------------------------------------------------------

    [Requires(ExportPermissions.Create)]
    public sealed record RequestExportCommand(ExportRequest Request) : ICommand<Guid>;

    public sealed class RequestExportCommandHandler(
        ExportCaller caller,
        ExportSourceRegistry sources,
        ExportRendererRegistry renderers,
        IExportJobRepository jobs,
        IExportAuditRepository audit,
        IExportClock clock)
        : ICommandHandler<RequestExportCommand, Guid>
    {
        public async Task<Guid> Handle(RequestExportCommand command, CancellationToken ct)
        {
            var (source, format, optionsJson) = await ExportRequestValidator.ValidateAsync(caller, sources, renderers, command.Request, ct);

            var now = clock.UtcNow;
            var job = ExportJobAggregate.Request(
                Guid.NewGuid(), caller.TenantId, caller.SiteId, caller.UserId, source.Key, format,
                command.Request!.ArgumentsJson, optionsJson, now);

            await jobs.AddAsync(job, ct);
            await audit.AddAsync(
                ExportAuditEntryAggregate.Record(Guid.NewGuid(), job.TenantId, job.SiteId, job.Id, caller.UserId, ExportAuditAction.Requested, now), ct);
            return job.Id;
        }
    }

    /// <summary>
    /// What an export request must be - for a one-off export and for a schedule alike, so a schedule can never hold
    /// a request the request path would refuse.
    /// </summary>
    internal static class ExportRequestValidator
    {
        public static async Task<(IExportSource Source, ExportFormat Format, string OptionsJson)> ValidateAsync(
            ExportCaller caller, ExportSourceRegistry sources, ExportRendererRegistry renderers, ExportRequest? request, CancellationToken ct)
        {
            if (request is null)
            {
                throw new ExportRequestRejectedException("An export request is required.");
            }

            // Exact lookup, never normalised: a near-miss key names nothing (see ExportSourceKeys).
            if (!sources.TryGet(request.SourceKey, out var source))
            {
                throw new ExportRequestRejectedException("That export source does not exist.");
            }

            // export:create let the caller in; the DATA has a permission of its own. The same answer as an unknown
            // source, so the source list cannot be probed by someone who may not read it.
            if (!await caller.HoldsAsync(source.RequiredPermission, ct))
            {
                throw new ExportRequestRejectedException("That export source does not exist.");
            }

            if (!Enum.TryParse<ExportFormat>(request.Format, ignoreCase: true, out var format) || !Enum.IsDefined(format) || !renderers.TryGet(format, out _))
            {
                throw new ExportRequestRejectedException("That export format is not available.");
            }

            // Nothing is opened at request time and the document is lazy, so "this export has two tables" cannot be
            // discovered here - the source DECLARES it (refutation 10). The CSV renderer fails the job as well.
            if (format == ExportFormat.Csv && source.TableCardinality == TableCardinality.Multiple)
            {
                throw new ExportRequestRejectedException("This source produces several tables, which CSV cannot hold. Choose XLSX or JSON.");
            }

            var delimiter = ',';
            if (!string.IsNullOrEmpty(request.CsvDelimiter))
            {
                if (request.CsvDelimiter.Length != 1 || !ExportRenderOptions.IsAllowedCsvDelimiter(request.CsvDelimiter[0]))
                {
                    throw new ExportRequestRejectedException("The CSV delimiter must be a comma, semicolon, tab or pipe.");
                }

                delimiter = request.CsvDelimiter[0];
            }

            // The page options are an allowlist too, and a value off it is REFUSED rather than quietly given A4: the
            // requester asked for something and would otherwise get something else with no sign of it.
            var pageSize = ExportRenderOptions.AllowedPdfPageSizes[0];
            if (!string.IsNullOrEmpty(request.PdfPageSize))
            {
                pageSize = ExportRenderOptions.CanonicalOrNull(request.PdfPageSize, ExportRenderOptions.AllowedPdfPageSizes)
                    ?? throw new ExportRequestRejectedException("The PDF page size must be one of: " + string.Join(", ", ExportRenderOptions.AllowedPdfPageSizes) + ".");
            }

            var orientation = ExportRenderOptions.AllowedPdfOrientations[0];
            if (!string.IsNullOrEmpty(request.PdfOrientation))
            {
                orientation = ExportRenderOptions.CanonicalOrNull(request.PdfOrientation, ExportRenderOptions.AllowedPdfOrientations)
                    ?? throw new ExportRequestRejectedException("The PDF orientation must be one of: " + string.Join(", ", ExportRenderOptions.AllowedPdfOrientations) + ".");
            }

            // Turning the formula guard OFF is a privileged act (refutation 11): any holder of export:create could
            // otherwise disable it for data they do not own, and the file is opened on someone else's machine.
            var neutralise = request.NeutraliseFormulas ?? true;
            if (!neutralise && !await caller.HoldsAsync(ExportPermissions.Manage, ct))
            {
                throw new ExportRequestRejectedException("Turning formula neutralisation off requires the export:manage permission.");
            }

            if (request.ArgumentsJson is { Length: > ExportJobAggregate.MaximumArgumentsLength })
            {
                throw new ExportRequestRejectedException($"The arguments are at most {ExportJobAggregate.MaximumArgumentsLength} characters.");
            }

            var rejection = source.Validate(new ExportSourceContext(caller.SiteId, caller.UserId, request.ArgumentsJson));
            if (rejection is not null)
            {
                throw new ExportRequestRejectedException(rejection);
            }

            var options = new ExportRenderOptions
            {
                CsvDelimiter = delimiter,
                IncludeHeader = request.IncludeHeader,
                JsonIndented = request.JsonIndented,
                PdfPageSize = pageSize,
                PdfOrientation = orientation,
                NeutraliseFormulas = neutralise,
            };

            return (source, format, ExportOptionsJson.Serialize(options));
        }
    }

    // ---- reads -----------------------------------------------------------------------------------------------------

    [Requires(ExportPermissions.View)]
    public sealed record ListMyExportsQuery : IQuery<IReadOnlyList<ExportJobView>>;

    public sealed class ListMyExportsQueryHandler(ExportCaller caller, IExportJobRepository jobs, IExportClock clock)
        : IQueryHandler<ListMyExportsQuery, IReadOnlyList<ExportJobView>>
    {
        public async Task<IReadOnlyList<ExportJobView>> Handle(ListMyExportsQuery request, CancellationToken ct)
        {
            return ExportWire.ToViews(await jobs.ListByRequesterAsync(caller.SiteId, caller.UserId, ct), clock.UtcNow);
        }
    }

    [Requires(ExportPermissions.Manage)]
    public sealed record ListSiteExportsQuery : IQuery<IReadOnlyList<ExportJobView>>;

    public sealed class ListSiteExportsQueryHandler(ExportCaller caller, IExportJobRepository jobs, IExportClock clock)
        : IQueryHandler<ListSiteExportsQuery, IReadOnlyList<ExportJobView>>
    {
        public async Task<IReadOnlyList<ExportJobView>> Handle(ListSiteExportsQuery request, CancellationToken ct)
        {
            return ExportWire.ToViews(await jobs.ListBySiteAsync(caller.SiteId, ct), clock.UtcNow);
        }
    }

    [Requires(ExportPermissions.View)]
    public sealed record GetExportQuery(Guid Id) : IQuery<ExportJobView?>;

    public sealed class GetExportQueryHandler(ExportCaller caller, IExportJobRepository jobs, IExportClock clock)
        : IQueryHandler<GetExportQuery, ExportJobView?>
    {
        public async Task<ExportJobView?> Handle(GetExportQuery request, CancellationToken ct)
        {
            var job = await caller.FindVisibleAsync(jobs, request.Id, ct);
            return job is null ? null : ExportWire.ToView(job, job.IsDownloadable(clock.UtcNow));
        }
    }

    /// <summary>The sources THIS caller may export from - a source whose data permission they lack is not listed.</summary>
    [Requires(ExportPermissions.Create)]
    public sealed record ListExportSourcesQuery : IQuery<IReadOnlyList<ExportSourceView>>;

    public sealed class ListExportSourcesQueryHandler(ExportCaller caller, ExportSourceRegistry sources)
        : IQueryHandler<ListExportSourcesQuery, IReadOnlyList<ExportSourceView>>
    {
        public async Task<IReadOnlyList<ExportSourceView>> Handle(ListExportSourcesQuery request, CancellationToken ct)
        {
            var visible = new List<ExportSourceView>();
            foreach (var key in sources.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                if (sources.TryGet(key, out var source) && await caller.HoldsAsync(source.RequiredPermission, ct))
                {
                    visible.Add(new ExportSourceView { Key = source.Key, SingleTable = source.TableCardinality == TableCardinality.Single });
                }
            }

            return visible;
        }
    }

    // ---- cancel ----------------------------------------------------------------------------------------------------

    [Requires(ExportPermissions.View)]
    public sealed record CancelExportCommand(Guid Id) : ICommand<bool>;

    public sealed class CancelExportCommandHandler(ExportCaller caller, IExportJobRepository jobs, IExportAuditRepository audit, IExportClock clock)
        : ICommandHandler<CancelExportCommand, bool>
    {
        /// <returns>False when there is no such export FOR THIS CALLER, or it has already finished.</returns>
        public async Task<bool> Handle(CancelExportCommand request, CancellationToken ct)
        {
            // The instance that is saved is the instance that was just read: the concurrency check compares against it.
            var job = await caller.FindVisibleAsync(jobs, request.Id, ct);
            if (job is null || job.Status is not (ExportJobStatus.Queued or ExportJobStatus.Rendering))
            {
                return false;
            }

            var now = clock.UtcNow;
            job.Cancel(now);

            try
            {
                // Through the concurrency check: a render in flight loses its next heartbeat and abandons the attempt.
                await jobs.UpdateAsync(job, ct);
            }
            catch (Stomme.Persistence.Abstractions.ConcurrencyConflictException)
            {
                // The job moved on (completed, failed, heartbeat) between the read and the write. Not cancelled.
                return false;
            }

            await audit.AddAsync(
                ExportAuditEntryAggregate.Record(Guid.NewGuid(), job.TenantId, job.SiteId, job.Id, caller.UserId, ExportAuditAction.Cancelled, now), ct);
            return true;
        }
    }

    // ---- download --------------------------------------------------------------------------------------------------

    /// <summary>What the controller needs to stream an artifact.</summary>
    public sealed record ExportDownload(string PhysicalPath, string ContentType, string DownloadName);

    /// <summary>A COMMAND, not a query: preparing a download writes the audit entry.</summary>
    /// <param name="Audit">False for the continuation of a download already audited (a Range request past byte 0).</param>
    [Requires(ExportPermissions.View)]
    public sealed record PrepareExportDownloadCommand(Guid Id, bool Audit = true) : ICommand<ExportDownload?>;

    public sealed class PrepareExportDownloadCommandHandler(
        ExportCaller caller,
        IExportJobRepository jobs,
        IExportAuditRepository audit,
        IFileRepository files,
        ExportsFolder exports,
        IExportClock clock)
        : ICommandHandler<PrepareExportDownloadCommand, ExportDownload?>
    {
        /// <returns>Null - one answer - for every reason the caller may not have this artifact.</returns>
        public async Task<ExportDownload?> Handle(PrepareExportDownloadCommand request, CancellationToken ct)
        {
            var now = clock.UtcNow;
            var job = await caller.FindVisibleAsync(jobs, request.Id, ct);
            if (job is null || !job.IsDownloadable(now))
            {
                return null;
            }

            var file = await files.GetFileAsync(job.FileId!.Value);

            // Refutation 14: folder Edit lets a file be MOVED to a public folder. A File id the job remembers is not
            // proof the file is still an export artifact - so this re-asserts it, and refuses otherwise.
            if (!await exports.ContainsAsync(job.SiteId, file))
            {
                return null;
            }

            var path = await files.GetFilePathAsync(file);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                return null;
            }

            // Once per DOWNLOAD, not per range request: the authorisation above runs every time, the audit only for
            // the request that starts the download.
            if (request.Audit)
            {
                await audit.AddAsync(
                    ExportAuditEntryAggregate.Record(Guid.NewGuid(), job.TenantId, job.SiteId, job.Id, caller.UserId, ExportAuditAction.Downloaded, now), ct);
            }

            var name = $"{job.SourceKey}-{job.RequestedOnUtc:yyyyMMdd-HHmmss}{job.FileExtension()}";
            return new ExportDownload(path, job.ContentType(), name);
        }
    }
}
