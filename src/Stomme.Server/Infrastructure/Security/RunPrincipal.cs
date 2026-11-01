#nullable enable
using System;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Who a headless piece of work acts as - a published pipeline run - when there is no request to say (BL-280, the
    /// owner's answer, 2026-10-30: a run acts as the person who published the pipeline, or who started it by hand). One per
    /// DI scope, empty until the run enters its site (<c>StommeSiteScope</c>); <see cref="PermissionRowAccessGuard"/> and
    /// <see cref="StommeCurrentUser"/> read it only when there is no <c>HttpContext</c>, so a request is never affected.
    /// </summary>
    /// <remarks>
    /// The user's rights are read from the STORED rows of the site (<see cref="IStoredRowPermissionEvaluator"/>), every
    /// time: a publisher who has since lost the right, or been deleted, is refused - fail closed, never a remembered "yes".
    /// A job acts as no one: nothing sets this outside a pipeline run.
    /// </remarks>
    public sealed class RunPrincipal
    {
        public int? SiteId { get; private set; }

        public int? UserId { get; private set; }

        public string? UserName { get; private set; }

        public bool IsSet => SiteId is > 0 && UserId is > 0;

        /// <summary>Acts as <paramref name="userId"/> on <paramref name="siteId"/> for the rest of this scope; a scope acts as one user only.</summary>
        public void ActAs(int siteId, int userId, string? userName = null)
        {
            if (siteId <= 0 || userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "A run acts as a real user on a real site.");
            }

            if (IsSet && (SiteId != siteId || UserId != userId))
            {
                throw new InvalidOperationException("This scope already acts as another user.");
            }

            SiteId = siteId;
            UserId = userId;
            UserName = userName;
        }
    }
}
