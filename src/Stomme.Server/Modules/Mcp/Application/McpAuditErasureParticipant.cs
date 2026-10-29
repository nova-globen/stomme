#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Mcp.Persistence;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Mcp.Application
{
    /// <summary>
    /// The MCP tool-call ledger: a SECURITY record of what was done through tools on the subject's account, so
    /// it is retained one year under Article 17(3) (the Security reason) and then deleted by the same job. Its
    /// rows hold the user id, the verb, the tool or resource name and an argument hash - never the arguments.
    /// </summary>
    public sealed class McpAuditErasureParticipant(IMcpAuditRepository audit, IUnitOfWork unitOfWork, TimeProvider clock) : ISubjectErasureParticipant
    {
        public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(365);

        public string Name => "MCP audit";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Retain("McpAuditEntryAggregate", RetentionReason.Security, RetentionPeriod) };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var count = (await audit.FindAsync(e => e.SiteId == subject.SiteId && e.UserId == subject.UserId, ct)).Count;
            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Retain, count, RetentionReason.Security, clock.GetUtcNow().UtcDateTime.Add(RetentionPeriod)) };
        }

        public async Task<int> PurgeRetainedAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await audit.FindAsync(e => e.SiteId == subject.SiteId && e.UserId == subject.UserId, ct);
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
    }
}
