#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// The <see cref="IAccessGuard"/> of the authorization bridge (D99, BL-99): a <c>resource:operation</c> key
    /// is decided by Stomme's EXISTING permission model — the Permission rows
    /// <c>(SiteId, EntityName = resource, EntityId = -1, PermissionName = operation)</c> of the site the
    /// current request resolved to, evaluated per request through <see cref="IUserPermissions"/> exactly as a
    /// page or module permission is (deny-first, then grant over the principal's role names / user id).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules, each of which a test pins (<c>PermissionRowAccessGuardTests</c>):
    /// <list type="bullet">
    /// <item>null/empty feature AND permission impose nothing — allowed, even outside a request.</item>
    /// <item>No <c>HttpContext</c> (a hosted job, a headless caller) and any requirement — DENIED. There is no
    /// principal to decide for; a system principal is a separate, security-relevant decision (D99).</item>
    /// <item>The key is EXACTLY two segments (a resource may itself be dotted, e.g. <c>platform.modules</c>,
    /// but never carry a second ':'); anything else is malformed and DENIED, never silently reinterpreted
    /// into some other resource and never mapped to a role check.</item>
    /// <item>Both halves are lower-cased: the rows are declared lower-case (D49, normalise the data — bare
    /// equality is case-sensitive on PostgreSQL and the in-memory <c>PermissionName</c> match is ordinal).</item>
    /// <item>The site is the request's resolved alias (<c>HttpContext.GetAlias()?.SiteId</c>), -1 when none.</item>
    /// <item>The lookup is the 5-argument <see cref="IUserPermissions.IsAuthorizedAsync(System.Security.Claims.ClaimsPrincipal, int, string, int, string)"/>
    /// — the overload WITHOUT the roles fallback — so a key with NO rows on the site is DENIED (the seeded
    /// Administrators row is what admits an administrator; there is no Host/Admin short-circuit, F1).</item>
    /// <item><c>system:admin</c> is the one key that is not a row: it means membership of
    /// <see cref="RoleNames.Host"/>.</item>
    /// <item>The feature axis goes through <see cref="IFeatureEvaluator"/> and is DENIED when no evaluator is
    /// registered — the bridge fails CLOSED (the spike's bridge failed open, F11).</item>
    /// </list>
    /// The permission axis is evaluated before the feature axis, so a denied caller never pays for a feature
    /// evaluation.
    /// </para>
    /// </remarks>
    public sealed class PermissionRowAccessGuard : IAccessGuard
    {
        /// <summary>The one key that maps to role membership rather than to Permission rows.</summary>
        public const string SystemAdminKey = "system:admin";

        /// <summary>Every key-derived row carries this EntityId: the key is the resource, not an instance of it.</summary>
        public const int KeyEntityId = -1;

        private readonly IHttpContextAccessor _accessor;
        private readonly IUserPermissions _userPermissions;
        private readonly IFeatureEvaluator? _features;

        // The evaluator is optional AT THE CONSTRUCTOR so a host that registered no IFeatureEvaluator at all
        // still composes — and then denies every feature-gated request rather than admitting it.
        public PermissionRowAccessGuard(IHttpContextAccessor accessor, IUserPermissions userPermissions, IFeatureEvaluator? features = null)
        {
            _accessor = accessor;
            _userPermissions = userPermissions;
            _features = features;
        }

        public async Task<bool> IsAllowedAsync(string? feature, string? permission, CancellationToken ct = default)
        {
            var requiresPermission = !string.IsNullOrWhiteSpace(permission);
            var requiresFeature = !string.IsNullOrWhiteSpace(feature);

            if (!requiresPermission && !requiresFeature)
            {
                return true;
            }

            var context = _accessor.HttpContext;
            if (context == null)
            {
                return false;
            }

            if (requiresPermission && !await IsPermissionHeldAsync(context, permission!))
            {
                return false;
            }

            if (requiresFeature)
            {
                if (_features == null)
                {
                    return false;
                }

                if (!await _features.IsEnabledAsync(feature!, ct))
                {
                    return false;
                }
            }

            return true;
        }

        public async Task<bool> IsAllowedAsync(MemberInfo target, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(target);

            foreach (var requirement in CollectRequirements(target))
            {
                if (!await IsAllowedAsync(requirement.Feature, requirement.Permission, ct))
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> IsPermissionHeldAsync(HttpContext context, string permission)
        {
            var key = permission.Trim();

            if (string.Equals(key, SystemAdminKey, StringComparison.OrdinalIgnoreCase))
            {
                return context.User?.IsInRole(RoleNames.Host) ?? false;
            }

            if (!TrySplit(key, out var resource, out var operation))
            {
                return false;
            }

            var siteId = context.GetAlias()?.SiteId ?? -1;

            // The five-argument overload: rows only, NO roles fallback. A key with no rows on this site is denied.
            return await _userPermissions.IsAuthorizedAsync(context.User, siteId, resource, KeyEntityId, operation);
        }

        /// <summary>
        /// Splits <c>resource:operation</c> into its lower-cased halves. False when the key has no ':', more
        /// than one ':', or an empty half.
        /// </summary>
        /// <remarks>
        /// Delegates to <see cref="PermissionKeys.TrySplit"/> so the guard splits a key exactly the way the
        /// declaration that produced the ROW split it. Two copies of this rule would be two chances for the
        /// lookup and the row to disagree, and a disagreement here is a silent denial.
        /// </remarks>
        public static bool TrySplit(string key, out string resource, out string operation)
        {
            return PermissionKeys.TrySplit(key, out resource, out operation);
        }

        // A method's gate is the union of its own [Requires] and its declaring type's [Requires], so a
        // class-level requirement applies to every gated member without being repeated.
        private static IEnumerable<RequiresAttribute> CollectRequirements(MemberInfo target)
        {
            var requirements = target.GetCustomAttributes<RequiresAttribute>(inherit: true);
            if (target is MethodInfo { DeclaringType: { } declaringType })
            {
                requirements = requirements.Concat(declaringType.GetCustomAttributes<RequiresAttribute>(inherit: true));
            }

            return requirements;
        }
    }
}
