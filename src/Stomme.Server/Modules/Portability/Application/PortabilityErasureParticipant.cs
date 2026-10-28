#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Portability.Persistence;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Portability.Application
{
    /// <summary>
    /// The portability ledgers (D107) under D112. A package built ABOUT the subject is their data and goes at once
    /// (Delete); a package the subject REQUESTED about someone else, and the audit lines of who asked for whose data,
    /// are the accountability record of the export itself - kept one year to establish or defend a claim about it
    /// (Article 17(3)(e), the LegalClaims reason), then deleted by the same job. The package's artifact file follows the
    /// package row through the module's own expiry.
    /// </summary>
    public sealed class PortabilityErasureParticipant(IPortabilityPackageRepository packages, IPortabilityAuditRepository audit, IUnitOfWork unitOfWork, TimeProvider clock) : ISubjectErasureParticipant
    {
        public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(365);

        public string Name => "Portability";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Delete("PortabilityPackageAggregate (subject)"),
            ErasureDeclaration.Retain("PortabilityPackageAggregate (requester)", RetentionReason.LegalClaims, RetentionPeriod),
            ErasureDeclaration.Retain("PortabilityAuditEntryAggregate", RetentionReason.LegalClaims, RetentionPeriod),
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await packages.ListForSubjectAsync(subject.SiteId, subject.UserId, ct);
            foreach (var package in mine)
            {
                await packages.DeleteAsync(package, ct);
            }

            if (mine.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            var due = clock.GetUtcNow().UtcDateTime.Add(RetentionPeriod);
            var requested = (await packages.FindAsync(p => p.SiteId == subject.SiteId && p.RequestedByUserId == subject.UserId, ct)).Count;
            var lines = (await AuditLinesAsync(subject, ct)).Count;
            return new[]
            {
                new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, mine.Count),
                new ErasureOutcome(Declarations[1].Store, ErasureHandling.Retain, requested, RetentionReason.LegalClaims, due),
                new ErasureOutcome(Declarations[2].Store, ErasureHandling.Retain, lines, RetentionReason.LegalClaims, due),
            };
        }

        public async Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var requested = await packages.FindAsync(p => p.SiteId == subject.SiteId && p.RequestedByUserId == subject.UserId, ct);
            foreach (var package in requested)
            {
                await packages.DeleteAsync(package, ct);
            }

            var lines = await AuditLinesAsync(subject, ct);
            foreach (var line in lines)
            {
                await audit.DeleteAsync(line, ct);
            }

            if (requested.Count + lines.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return requested.Count + lines.Count;
        }

        private async Task<List<Domain.PortabilityAuditEntryAggregate>> AuditLinesAsync(ErasureSubject subject, CancellationToken ct)
        {
            var asRequester = await audit.FindAsync(a => a.SiteId == subject.SiteId && a.RequestedByUserId == subject.UserId, ct);
            var asSubject = await audit.FindAsync(a => a.SiteId == subject.SiteId && a.SubjectUserId == subject.UserId, ct);
            return asRequester.Concat(asSubject).GroupBy(a => a.Id).Select(g => g.First()).ToList();
        }
    }
}
