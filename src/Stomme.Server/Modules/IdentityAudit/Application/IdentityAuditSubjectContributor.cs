#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.IdentityAudit.Persistence;
using Stomme.Portability;

namespace Stomme.Modules.IdentityAudit.Application
{
    /// <summary>
    /// The identity audit lines ABOUT a person on a site, for their data package: what happened to
    /// their account, when and from where. Lines where they were only the ACTOR on someone else's account are that other
    /// person's data and are not included.
    /// </summary>
    public sealed class IdentityAuditSubjectContributor(IIdentityAuditRepository audit) : ISubjectDataContributor
    {
        public const string Type = "identity-audit";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await audit.FindAsync(entry => entry.SiteId == scope.SiteId && entry.SubjectUserId == scope.UserId, cancellationToken))
                .OrderBy(entry => entry.OccurredOnUtc)
                .Select(entry => new PortableEntity(
                    new PortableEntityRef(Type, entry.Id.ToString("N", CultureInfo.InvariantCulture)),
                    entry.Event.ToString(),
                    1,
                    JsonSerializer.Serialize(new
                    {
                        @event = entry.Event.ToString(),
                        occurredOnUtc = entry.OccurredOnUtc,
                        byUserId = entry.ActorUserId,
                        remoteAddress = entry.RemoteAddress,
                        detail = entry.Detail,
                    })))
                .ToList();
    }
}