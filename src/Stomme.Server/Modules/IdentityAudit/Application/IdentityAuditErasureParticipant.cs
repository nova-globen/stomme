#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.IdentityAudit.Persistence;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.IdentityAudit.Application
{
    /// <summary>
    /// The identity audit ledger (sign-ins, lockouts, password and role changes about the subject) is a SECURITY record:
    /// retained unchanged for one year under Article 17(3) (the Security reason of the closed set), then deleted by the
    /// same job. The subject appears as the actor or as the subject of an entry; both are theirs.
    /// </summary>
    public sealed class IdentityAuditErasureParticipant(IIdentityAuditRepository audit, IUnitOfWork unitOfWork, TimeProvider clock) : ISubjectErasureParticipant
    {
        public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(365);

        public string Name => "Identity audit";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Retain("IdentityAuditEntryAggregate", RetentionReason.Security, RetentionPeriod) with { Summary = "the security record of your sign-ins and account changes" } };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var count = (await MineAsync(subject, ct)).Count;
            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Retain, count, RetentionReason.Security, clock.GetUtcNow().UtcDateTime.Add(RetentionPeriod)) };
        }

        public async Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await MineAsync(subject, ct);
            foreach (var entry in mine)
            {
                await audit.DeleteAsync(entry, ct);
            }

            if (mine.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return mine.Count;
        }

        private async Task<List<Domain.IdentityAuditEntryAggregate>> MineAsync(ErasureSubject subject, CancellationToken ct)
        {
            var asSubject = await audit.FindAsync(e => e.SiteId == subject.SiteId && e.SubjectUserId == subject.UserId, ct);
            var asActor = await audit.FindAsync(e => e.SiteId == subject.SiteId && e.ActorUserId == subject.UserId, ct);
            return asSubject.Concat(asActor).GroupBy(e => e.Id).Select(g => g.First()).ToList();
        }
    }
}
