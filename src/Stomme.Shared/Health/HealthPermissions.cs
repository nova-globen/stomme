using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Health
{
    /// <summary>
    /// The Health module's permission KEYS, decided by the authorization bridge: each key is a
    /// site Permission row <c>(EntityName = "health", EntityId = -1, PermissionName = operation)</c>, seeded from the
    /// module definition's <c>PermissionNames</c> and consulted by the mediator's <c>[Requires]</c>, the controller's
    /// <c>[RequiresPermissionKey]</c> and the Client's <c>ModuleBase.IsAuthorizedPermission</c> alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in <c>Stomme.Shared</c> because both halves need it: the Client <c>ModuleInfo</c> declares it and gates
    /// its controls with it, the Server's requests and controller carry it.
    /// </para>
    /// <para>
    /// <b>Two keys, and no <c>ops:*</c> key anywhere.</b> the predecessor platform declared <c>health:view</c> and
    /// <c>health:manage</c> — of which <c>manage</c> gated nothing at all — and separately gated its realtime hub on
    /// <c>ops:view</c>, a key with no declaration and no producer. D109 settled that direction: every operations
    /// surface maps to the Host role and no <c>ops:*</c> key exists. Here <see cref="View"/> reads this site's
    /// health, and <see cref="Manage"/> is the key that can actually do something — acknowledge an alert, change a
    /// threshold, run the probes now.
    /// </para>
    /// <para>
    /// <b>Neither key is enough to see everything.</b> Two things sit ABOVE them and are Host-only, because they are
    /// installation-wide rather than this site's: the diagnostics endpoint, and the privileged
    /// <c>LastDescription</c> of a Tenant- or Installation-scope subject — which carries the raw exception text of a
    /// shared dependency, host and database and user included. A site's operator sees the STATE of a shared
    /// dependency, not its connection details.
    /// </para>
    /// </remarks>
    public static class HealthPermissions
    {
        /// <summary>The key resource — the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "health";

        /// <summary>Read this site's probe results, alert ledger and rollup history.</summary>
        public const string View = "health:view";

        /// <summary>Acknowledge an alert, change a threshold, run the probes now.</summary>
        public const string Manage = "health:manage";

        /// <summary>Every key, in declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        /// <summary>
        /// Every key with its DEFAULT roles — what the module definition declares and a fresh site seeds. The per-key
        /// <c>PermissionKeys.Declare</c> appends Administrators to each entry, so both seed to Administrators only:
        /// health is an operator's view, and nothing here is self-service.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new string[0]),
            (Manage, new string[0]),
        };
    }
}
