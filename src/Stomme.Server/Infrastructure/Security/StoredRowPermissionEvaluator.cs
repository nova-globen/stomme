#nullable enable
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Decides a <c>resource:operation</c> key for a user who is NOT present: no request, no principal, no claims.
    /// </summary>
    public interface IStoredRowPermissionEvaluator
    {
        /// <summary>
        /// Whether <paramref name="userId"/> holds <paramref name="permissionKey"/> on <paramref name="siteId"/>
        /// RIGHT NOW, judged from stored rows alone.
        /// </summary>
        Task<bool> IsAuthorizedAsync(int siteId, int userId, string permissionKey, CancellationToken ct = default);
    }

    /// <summary>
    /// The headless twin of <see cref="PermissionRowAccessGuard"/> (D108 refutation 8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a twin exists at all.</b> The request-path guard decides over a <c>ClaimsPrincipal</c>, and refuses
    /// outright when there is no <c>HttpContext</c> - correctly, because a hosted job has nobody to decide for. But
    /// an export is rendered by a job, minutes after it was requested, AS its requester; the question "may this
    /// user still read this data?" has to be answerable without them being there.
    /// </para>
    /// <para>
    /// <b>What is shared, and the one thing that differs.</b> The decision itself is
    /// <see cref="UserSecurity.IsAuthorized(User, string, System.Collections.Generic.List{Permission})"/> - the SAME
    /// deny-first evaluation over the SAME Permission rows <c>(SiteId, EntityName = resource, EntityId = -1,
    /// PermissionName = operation)</c>, reached through the same no-fallback lookup, so a key with no rows on the
    /// site is denied here too. What differs is where the user's ROLES come from: the request path takes the role
    /// names in the principal's CLAIMS and filters them by stored effective dates, admitting a claimed role that
    /// has no stored row at all; this path takes the stored <c>UserRole</c> rows and nothing else.
    /// </para>
    /// <para>
    /// <b>The two divergences that follow are intended, and are the only ones</b>
    /// (<c>ExportHeadlessAuthorizationTests</c> runs the matrix against the request path to prove it):
    /// a role that exists only as a claim counts on the request path and not here; and a role revoked since the
    /// cookie was issued still counts on the request path until the cookie is refreshed, and not here. For a job
    /// the stored rows are the authority - both divergences are this path being STRICTER.
    /// </para>
    /// <para>
    /// One thing is carried over FROM sign-in rather than from rows, because it is a rule and not a fact about the
    /// user: a stored Host role implies Administrators and Registered Users, exactly as
    /// <c>UserSecurity.CreateClaimsIdentity</c> implies them.
    /// </para>
    /// <para>
    /// <c>system:admin</c> is the one key that is not a row: on the request path it means the principal is in the
    /// Host role, and here it means a stored, effective Host <c>UserRole</c>.
    /// </para>
    /// </remarks>
    public sealed class StoredRowPermissionEvaluator : IStoredRowPermissionEvaluator
    {
        private readonly IUserRepository _users;
        private readonly IUserRoleRepository _userRoles;
        private readonly IPermissionRepository _permissions;

        public StoredRowPermissionEvaluator(IUserRepository users, IUserRoleRepository userRoles, IPermissionRepository permissions)
        {
            _users = users ?? throw new ArgumentNullException(nameof(users));
            _userRoles = userRoles ?? throw new ArgumentNullException(nameof(userRoles));
            _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        }

        public async Task<bool> IsAuthorizedAsync(int siteId, int userId, string permissionKey, CancellationToken ct = default)
        {
            if (siteId <= 0 || userId <= 0 || string.IsNullOrWhiteSpace(permissionKey))
            {
                return false;
            }

            // A deleted account holds nothing, whatever rows still name it.
            var account = await _users.GetUserAsync(userId);
            if (account is null || account.IsDeleted)
            {
                return false;
            }

            var roles = (await _userRoles.GetUserRolesAsync(userId, siteId))
                .Where(item => item.Role is not null && Utilities.IsEffectiveAndNotExpired(item.EffectiveDate, item.ExpiryDate))
                .Select(item => item.Role.Name)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // "Host users are site admins by default": sign-in gives a Host user the Administrators and Registered Users
            // role claims (UserSecurity.CreateClaimsIdentity). That is a framework RULE derived from a stored Host
            // role, not a claim nothing backs - so it is applied here from the same stored row, or a Host user would be
            // refused every key that is granted to Administrators.
            if (roles.Contains(RoleNames.Host, StringComparer.Ordinal))
            {
                foreach (var implied in new[] { RoleNames.Admin, RoleNames.Registered })
                {
                    if (!roles.Contains(implied, StringComparer.Ordinal))
                    {
                        roles.Add(implied);
                    }
                }
            }

            var key = permissionKey.Trim();
            if (string.Equals(key, PermissionRowAccessGuard.SystemAdminKey, StringComparison.OrdinalIgnoreCase))
            {
                return roles.Contains(RoleNames.Host, StringComparer.Ordinal);
            }

            // Split exactly the way the declaration that produced the ROW was split; malformed is denied.
            if (!PermissionRowAccessGuard.TrySplit(key, out var resource, out var operation))
            {
                return false;
            }

            var rows = (await _permissions.GetPermissionsAsync(siteId, resource, PermissionRowAccessGuard.KeyEntityId, operation)).ToList();

            var user = new User
            {
                UserId = userId,
                Username = account.Username,
                IsAuthenticated = true,
                Roles = FormatRoles(roles),
            };

            return UserSecurity.IsAuthorized(user, operation, rows);
        }

        // The ";a;b;" spelling UserPermissions builds, so the shared evaluation splits it identically.
        private static string FormatRoles(System.Collections.Generic.IReadOnlyCollection<string> roles)
        {
            if (roles.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(";");
            foreach (var role in roles)
            {
                builder.Append(role).Append(';');
            }

            return builder.ToString();
        }
    }
}
