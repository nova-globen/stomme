using System;
using Stomme.Security;
using Microsoft.AspNetCore.Authorization;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// The authorization requirement of a lifted <c>resource:operation</c> permission KEY (D99 point 9,
    /// BL-99) - the controller/hub counterpart of <see cref="PermissionRowAccessGuard"/>, which decides the
    /// same keys on the mediator path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists BESIDE <c>PermissionRequirement</c> rather than reusing it, because that requirement's
    /// handler does two things a lifted key must never do (F8, measured): it lets the CALLER re-scope the row
    /// lookup with a <c>?auth&lt;entity&gt;id=</c> query-string parameter, and it passes a roles list to the
    /// six-argument <c>IUserPermissions</c> overload, which falls back to ROLE MEMBERSHIP when the entity has
    /// no rows. A key with no rows must be DENIED, and a key is never scoped to an instance - its rows always
    /// carry <c>EntityId = -1</c>.
    /// </para>
    /// <para>
    /// The key is parsed ONCE here, when the policy is built, so a malformed key cannot be re-interpreted per
    /// request: <see cref="IsWellFormed"/> is false and <see cref="PermissionKeyHandler"/> never succeeds.
    /// </para>
    /// </remarks>
    public sealed class PermissionKeyRequirement : IAuthorizationRequirement
    {
        public PermissionKeyRequirement(string key)
        {
            Key = key ?? "";
            IsSystemAdmin = string.Equals(Key.Trim(), PermissionRowAccessGuard.SystemAdminKey, StringComparison.OrdinalIgnoreCase);
            if (!IsSystemAdmin && PermissionKeys.TrySplit(Key, out var resource, out var operation))
            {
                Resource = resource;
                Operation = operation;
            }
            else
            {
                Resource = "";
                Operation = "";
            }
        }

        /// <summary>The key exactly as the attribute (or hub policy) declared it.</summary>
        public string Key { get; }

        /// <summary>The lower-cased first half - the Permission row's <c>EntityName</c>.</summary>
        public string Resource { get; }

        /// <summary>The lower-cased second half - the Permission row's <c>PermissionName</c>.</summary>
        public string Operation { get; }

        /// <summary><c>system:admin</c>: the one key that means Host membership rather than a row.</summary>
        public bool IsSystemAdmin { get; }

        /// <summary>False for a key that is not <c>resource:operation</c> - such a requirement can only deny.</summary>
        public bool IsWellFormed => IsSystemAdmin || (Resource.Length > 0 && Operation.Length > 0);

        public override string ToString() => "PermissionKey(" + Key + ")";
    }
}