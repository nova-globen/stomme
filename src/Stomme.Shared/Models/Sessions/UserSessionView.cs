using System;

namespace Stomme.Models.Sessions
{
    /// <summary>
    /// One signed-in session as its owner or an administrator sees it (BL-119). <see cref="IsCurrent"/> marks the session
    /// the request itself was made under, so a page can say "this device" and not offer to revoke it by accident.
    /// </summary>
    public sealed record UserSessionView(
        Guid Id,
        int UserId,
        string Username,
        DateTime CreatedOnUtc,
        DateTime LastSeenOnUtc,
        DateTime ExpiresOnUtc,
        string RemoteAddress,
        string UserAgent,
        bool IsCurrent);
}
