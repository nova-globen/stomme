#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>
    /// The notification requests (the module's send log) on erasure: a request names its recipients as
    /// <c>User:&lt;id&gt;</c> entries, so the subject's entries are REMOVED from every request on the site (Anonymise -
    /// the request, its template, parameters and delivery attempts stay as the record of a send, with no link to the
    /// person), and a request left with no recipient at all is deleted. The recipients are a value-object list, not a
    /// column, so the site's requests are read and filtered in memory; the module's own retention bounds them.
    /// </summary>
    public sealed class NotificationRequestErasureParticipant(INotificationRequestRepository requests, IUnitOfWork unitOfWork) : ISubjectErasureParticipant
    {
        public string Name => "Notification requests";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Anonymise("NotificationRequestAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var touched = 0;
            foreach (var request in await requests.ListBySiteAsync(subject.SiteId, ct))
            {
                if (!request.RemoveRecipient(subject.UserId))
                {
                    continue;
                }

                touched++;
                if (request.Recipients.Count == 0)
                {
                    await requests.DeleteAsync(request, ct);
                }
                else
                {
                    await requests.UpdateAsync(request, ct);
                }
            }

            if (touched > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Anonymise, touched) };
        }
    }
}
