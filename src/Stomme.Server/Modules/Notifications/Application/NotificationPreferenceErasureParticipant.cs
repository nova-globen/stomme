#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Erasure;
using Stomme.Modules.Notifications.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Notifications.Application
{
    /// <summary>The subject's notification preferences on the site (channels, quiet hours, topic mutes): deleted.</summary>
    public sealed class NotificationPreferenceErasureParticipant(IRepository<NotificationPreferenceAggregate, System.Guid> preferences, IUnitOfWork unitOfWork) : ISubjectErasureParticipant
    {
        public string Name => "Notification preferences";

        public IReadOnlyList<ErasureDeclaration> Declarations { get; } = new[] { ErasureDeclaration.Delete("NotificationPreferenceAggregate") };

        public async Task<IReadOnlyList<ErasureOutcome>> EraseAsync(ErasureSubject subject, CancellationToken ct = default)
        {
            var mine = await preferences.FindAsync(p => p.SiteId == subject.SiteId && p.UserId == subject.UserId, ct);
            foreach (var preference in mine)
            {
                await preferences.DeleteAsync(preference, ct);
            }

            if (mine.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return new[] { new ErasureOutcome(Declarations[0].Store, ErasureHandling.Delete, mine.Count) };
        }
    }
}
