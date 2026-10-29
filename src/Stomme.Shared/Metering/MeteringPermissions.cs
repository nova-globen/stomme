using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Metering
{
    /// <summary>
    /// The Metering module's permission KEYS, decided by the authorization bridge: each key is a
    /// site Permission row <c>(EntityName = "metering", EntityId = -1, PermissionName = operation)</c>, seeded from
    /// the module definition's <c>PermissionNames</c> and consulted by the mediator's <c>[Requires]</c>, the
    /// controller's <c>[RequiresPermissionKey]</c> and the Client's <c>ModuleBase.IsAuthorizedPermission</c> alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in <c>Stomme.Shared</c> because both halves need it: the Client <c>ModuleInfo</c> declares it and gates
    /// its controls with it, the Server's requests and controller carry it.
    /// </para>
    /// <para>
    /// <b>Three keys, one of them the self-service read.</b> Hand-checking
    /// <c>HasClaim</c> on usage pages, or keys that do not separate "my own consumption" from "the
    /// site's", or an override command with NO key that takes the tenant from the request body, are the traps avoided. Here
    /// <see cref="ViewOwn"/> is the self-service key every registered user holds, <see cref="View"/> is the
    /// operator's read of the whole site, and <see cref="Manage"/> is the only key that can define a quota, reset a
    /// counter or grant an override - and every by-id handler behind it resolves through the caller's own site.
    /// </para>
    /// </remarks>
    public static class MeteringPermissions
    {
        /// <summary>The key resource - the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "metering";

        /// <summary>Read the caller's OWN consumption on this site.</summary>
        public const string ViewOwn = "metering:view-own";

        /// <summary>Read the site's usage log, its AI dashboard and its quota definitions.</summary>
        public const string View = "metering:view";

        /// <summary>Define, redefine, suspend, reset and override quotas.</summary>
        public const string Manage = "metering:manage";

        /// <summary>Every key, in declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { ViewOwn, View, Manage };

        /// <summary>
        /// Every key with its DEFAULT roles - what the module definition declares and a fresh site seeds. The per-key
        /// <c>PermissionKeys.Declare</c> appends Administrators to each entry, so <see cref="ViewOwn"/> seeds to
        /// Registered Users AND Administrators and the other two to Administrators only.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (ViewOwn, new[] { RoleNames.Registered }),
            (View, new string[0]),
            (Manage, new string[0]),
        };
    }
}
