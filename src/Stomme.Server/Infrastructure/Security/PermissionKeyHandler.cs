using System.Threading.Tasks;
using Stomme.Enums;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Decides a <see cref="PermissionKeyRequirement"/> the way the permission model requires: ALWAYS against the site's
    /// Permission rows <c>(EntityName = resource, EntityId = -1, PermissionName = operation)</c>, through the
    /// FIVE-argument <see cref="IUserPermissions"/> overload - the one with no roles fallback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately NOT what <c>PermissionHandler</c> does, and the differences are the point (F8):
    /// <list type="bullet">
    /// <item>it reads NO query string - there is no <c>?auth&lt;entity&gt;id=</c> re-scoping, so a caller
    /// cannot point the lookup at a different entity id than the one the key is stored under;</item>
    /// <item>it passes no roles, so a key with NO rows on this site is DENIED rather than falling back to role
    /// membership (an administrator is admitted by the SEEDED Administrators row, not by being an
    /// administrator - there is no Host/Admin short-circuit anywhere in Stomme's decision, F1);</item>
    /// <item><c>system:admin</c> is the single key that is role membership (Host) rather than a row, matching
    /// <see cref="PermissionRowAccessGuard"/> exactly.</item>
    /// </list>
    /// </para>
    /// <para>
    /// A denial only fails to <c>Succeed</c> - it never throws - so the MVC/SignalR pipeline produces its
    /// normal 403 (or a challenge for an anonymous caller), and the failure is logged at Error, the way
    /// <c>PermissionHandler</c> logs one.
    /// </para>
    /// </remarks>
    public sealed class PermissionKeyHandler : AuthorizationHandler<PermissionKeyRequirement>
    {
        private readonly IHttpContextAccessor _accessor;
        private readonly IUserPermissions _userPermissions;
        private readonly ILogManager _logger;

        public PermissionKeyHandler(IHttpContextAccessor accessor, IUserPermissions userPermissions, ILogManager logger)
        {
            _accessor = accessor;
            _userPermissions = userPermissions;
            _logger = logger;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionKeyRequirement requirement)
        {
            if (!requirement.IsWellFormed)
            {
                Deny(context, requirement);
                return;
            }

            if (requirement.IsSystemAdmin)
            {
                if (context.User != null && context.User.IsInRole(RoleNames.Host))
                {
                    context.Succeed(requirement);
                }
                else
                {
                    Deny(context, requirement);
                }

                return;
            }

            // No request means no resolved site and no principal to decide for: denied, never admitted
            // (a hosted job has no principal - granting one is a separate, security-relevant decision).
            var ctx = _accessor.HttpContext;
            if (ctx == null)
            {
                Deny(context, requirement);
                return;
            }

            var siteId = ctx.GetAlias() != null ? ctx.GetAlias().SiteId : -1;

            if (await _userPermissions.IsAuthorizedAsync(context.User, siteId, requirement.Resource, PermissionKeys.KeyEntityId, requirement.Operation))
            {
                context.Succeed(requirement);
            }
            else
            {
                Deny(context, requirement);
            }
        }

        private void Deny(AuthorizationHandlerContext context, PermissionKeyRequirement requirement)
        {
            var user = context.User?.Identity?.Name ?? "";
            _logger.Log(SecurityRefusals.Level(context.User), this, LogFunction.Security, "User {User} Does Not Hold Permission Key {Key}", user, requirement.Key);
        }
    }
}