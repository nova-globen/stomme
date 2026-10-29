using System;
using Stomme.Infrastructure.Backup;
using Stomme.Persistence.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The audit context of the current request: the signed-in name, or the empty string when there is no
    /// request or no user - the same answer <c>DbContextUtils</c> gives the relational path - and the same
    /// <see cref="AuditStampSuppression"/> scope, so a write that must land unstamped (a restore, an erasure's
    /// placeholder) lands unstamped on RavenDB as it does on EF. One instance serves the singleton document store;
    /// the accessor is ambient (AsyncLocal), so it answers per request.
    /// </summary>
    public sealed class RequestAuditContext(IHttpContextAccessor accessor) : IAuditContext
    {
        public string CurrentUsername => accessor?.HttpContext?.User?.Identity?.Name ?? string.Empty;

        public DateTime UtcNow => DateTime.UtcNow;

        public bool IsSuppressed => AuditStampSuppression.IsActive;
    }
}
