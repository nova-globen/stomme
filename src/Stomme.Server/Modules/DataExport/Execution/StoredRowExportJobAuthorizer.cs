#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Infrastructure.Security;
using Stomme.Modules.DataExport.Domain;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>
    /// Re-authorises a queued export from STORED rows at the moment it is rendered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both checks made when the export was requested are made again, because minutes - or, behind a long queue,
    /// hours - may have passed: the requester must still hold <see cref="ExportPermissions.Create"/>, AND still hold
    /// the source's own <see cref="IExportSource.RequiredPermission"/> for the data. A user whose access was
    /// revoked after they queued an export does not get the file.
    /// </para>
    /// <para>
    /// The site and the requester come from the JOB ROW, never from the ambient request - there is none.
    /// </para>
    /// </remarks>
    public sealed class StoredRowExportJobAuthorizer : IExportJobAuthorizer
    {
        private readonly IStoredRowPermissionEvaluator _evaluator;

        public StoredRowExportJobAuthorizer(IStoredRowPermissionEvaluator evaluator)
        {
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        }

        public async Task<bool> IsAuthorizedAsync(ExportJobAggregate job, IExportSource source, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(job);
            ArgumentNullException.ThrowIfNull(source);

            // A source with no permission is refused by the registry; refuse here too rather than read "no
            // requirement" as "allowed".
            if (string.IsNullOrWhiteSpace(source.RequiredPermission))
            {
                return false;
            }

            return await _evaluator.IsAuthorizedAsync(job.SiteId, job.RequesterUserId, ExportPermissions.Create, ct)
                && await _evaluator.IsAuthorizedAsync(job.SiteId, job.RequesterUserId, source.RequiredPermission, ct);
        }
    }
}
