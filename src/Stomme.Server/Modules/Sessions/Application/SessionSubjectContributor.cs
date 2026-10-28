#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Portability;
using Stomme.Sessions.Persistence;

namespace Stomme.Modules.Sessions.Application
{
    /// <summary>
    /// A person's sessions on a site, for their data package (BL-82 over BL-119): when and from where they signed in, and
    /// when each session ended. The session id is the row's own - not a credential: a cookie is needed to use one.
    /// </summary>
    public sealed class SessionSubjectContributor(IUserSessionRepository sessions) : ISubjectDataContributor
    {
        public const string Type = "session";

        public string SubjectDataType => Type;

        public async Task<IReadOnlyList<PortableEntity>> ExportForSubjectAsync(SubjectScope scope, CancellationToken cancellationToken = default) =>
            (await sessions.FindAsync(session => session.SiteId == scope.SiteId && session.UserId == scope.UserId, cancellationToken))
                .OrderBy(session => session.CreatedOnUtc)
                .Select(session => new PortableEntity(
                    new PortableEntityRef(Type, session.Id.ToString("N", CultureInfo.InvariantCulture)),
                    session.CreatedOnUtc.ToString("u", CultureInfo.InvariantCulture),
                    1,
                    JsonSerializer.Serialize(new
                    {
                        signedInOnUtc = session.CreatedOnUtc,
                        lastSeenOnUtc = session.LastSeenOnUtc,
                        expiresOnUtc = session.ExpiresOnUtc,
                        remoteAddress = session.RemoteAddress,
                        userAgent = session.UserAgent,
                        revokedOnUtc = session.RevokedOnUtc,
                        revokeReason = session.RevokeReason,
                    })))
                .ToList();
    }
}