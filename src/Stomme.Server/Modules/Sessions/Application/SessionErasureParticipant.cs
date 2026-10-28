#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Sessions.Persistence;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Sessions.Application
{
    /// <summary>The subject's sessions on the site (address, agent, timings): deleted (D112).</summary>
    public sealed class SessionErasureParticipant(IUserSessionRepository sessions, IUnitOfWork unitOfWork) : ISubjectErasureParticipant
    {
        public string Name => "Sessions";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("UserSessionAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await sessions.FindAsync(s => s.SiteId == subject.SiteId && s.UserId == subject.UserId, ct);
            foreach (var session in mine)
            {
                await sessions.DeleteAsync(session, ct);
            }

            if (mine.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, mine.Count) };
        }
    }
}
