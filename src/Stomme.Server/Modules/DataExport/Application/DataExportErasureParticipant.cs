#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.DataExport.Persistence;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.DataExport.Application
{
    /// <summary>
    /// The data-export ledgers: the subject's export jobs are operational rows the module keeps for days
    /// and go at once (Delete; the exported file follows the job through the module's own retention); the export
    /// audit - who exported what - is a SECURITY record, retained one year (Article 17(3)) and then deleted by the
    /// same job. The subject's export SCHEDULES go too: a schedule runs as its owner, and with the owner gone it
    /// could never run again.
    /// </summary>
    public sealed class DataExportErasureParticipant(IExportJobRepository jobs, IExportAuditRepository audit, IExportScheduleRepository schedules, IUnitOfWork unitOfWork, TimeProvider clock) : ISubjectErasureParticipant
    {
        public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(365);

        public string Name => "Data export";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Delete("ExportJobAggregate") with { Summary = "your data exports and their files" },
            ErasureDeclaration.Retain("ExportAuditEntryAggregate", RetentionReason.Security, RetentionPeriod) with { Summary = "the record of the data exports you made" },
            ErasureDeclaration.Delete("ExportScheduleAggregate") with { Summary = "your scheduled data exports" },
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await jobs.ListByRequesterAsync(subject.SiteId, subject.UserId, ct);
            foreach (var job in mine)
            {
                await jobs.DeleteAsync(job, ct);
            }

            var owned = await schedules.ListByOwnerAsync(subject.SiteId, subject.UserId, ct);
            foreach (var schedule in owned)
            {
                await schedules.DeleteAsync(schedule, ct);
            }

            if (mine.Count > 0 || owned.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            var lines = (await audit.FindAsync(a => a.SiteId == subject.SiteId && a.ActorUserId == subject.UserId, ct)).Count;
            return new[]
            {
                new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, mine.Count),
                new ErasureOutcome(Declarations[1].Store, ErasureHandling.Retain, lines, RetentionReason.Security, clock.GetUtcNow().UtcDateTime.Add(RetentionPeriod)),
                new ErasureOutcome(Declarations[2].Store, ErasureHandling.Delete, owned.Count),
            };
        }

        public async Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var lines = await audit.FindAsync(a => a.SiteId == subject.SiteId && a.ActorUserId == subject.UserId, ct);
            foreach (var line in lines)
            {
                await audit.DeleteAsync(line, ct);
            }

            if (lines.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return lines.Count;
        }
    }
}
