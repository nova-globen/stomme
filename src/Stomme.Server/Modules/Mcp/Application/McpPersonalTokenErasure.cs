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
    /// <summary>A person's personal MCP tokens go with them: deleted when they are erased (a deleted user's token is refused anyway).</summary>
    public sealed class McpPersonalTokenErasure(IMcpPersonalTokenRepository tokens, IUnitOfWork unitOfWork) : ISubjectErasureParticipant
    {
        public string Name => "Personal MCP tokens";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[]
        {
            ErasureDeclaration.Delete("McpPersonalTokenAggregate") with { Summary = "the tokens your AI tools signed in with" },
        };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (subject.UserId <= 0 || subject.SiteId <= 0)
            {
                return Array.Empty<ErasureOutcome>();
            }

            var deleted = 0;
            foreach (var token in await tokens.ListForUserAsync(subject.SiteId, subject.UserId, ct))
            {
                await tokens.DeleteAsync(token, ct);
                deleted++;
            }

            if (deleted > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, deleted) };
        }
    }
}