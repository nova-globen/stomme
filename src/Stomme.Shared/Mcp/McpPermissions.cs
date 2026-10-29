using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Mcp
{
    /// <summary>
    /// The MCP module's permission KEYS, decided by the authorization bridge: each key is a
    /// site Permission row <c>(EntityName = "mcp", EntityId = -1, PermissionName = operation)</c>, seeded from the module
    /// definition's <c>PermissionNames</c> and consulted by <c>[Requires]</c>, the controller's
    /// <c>[RequiresPermissionKey]</c> and the Client's <c>ModuleBase.IsAuthorizedPermission</c> alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in <c>Stomme.Shared</c> because BOTH halves need it: the Client <c>ModuleInfo</c> DECLARES it (a key no
    /// module declares has no seeded rows, so a gate on it denies everyone - correct, and useless), the Client's controls
    /// gate themselves with it, and the Server's controller and contributors carry it.
    /// </para>
    /// <para>
    /// <b>There are THREE keys, not just <c>mcp:view</c> and <c>mcp:manage</c></b>, and the extra one is a
    /// SPLIT, not an addition: a single view key would gate both the published catalog and the AUDIT LEDGER on
    /// <c>mcp:view</c>. The catalog shows a user what THEY may use - every descriptor in it has already passed its own
    /// gate for that caller. The ledger shows EVERY user's calls on the site, arguments hashed but subjects and
    /// principals in plain sight. One key over both would mix a user's own view with the whole site's, as a single
    /// <c>notifications:view</c> gating a user's own inbox and the whole site's send log together would. <see cref="ViewAudit"/>
    /// is the operator's key; <see cref="View"/> is the one a user may hold.
    /// </para>
    /// <para>
    /// <b><see cref="Manage"/> is one key over all six registration operations</b> and is deliberately NOT split further.
    /// Register, refresh, enable, disable, remove and set-key are one job - deciding which external servers this site
    /// talks to - and anyone who can register a server can already reach everything a narrower key would have protected.
    /// A split that does not change what a holder can reach is paperwork, not a boundary.
    /// </para>
    /// </remarks>
    public static class McpPermissions
    {
        /// <summary>The key resource - the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "mcp";

        /// <summary>
        /// See what this install publishes over MCP, as the caller may use it. The gate on the built-in
        /// <c>stomme://site</c> resource, and on the catalog page.
        /// </summary>
        public const string View = "mcp:view";

        /// <summary>
        /// Read the site's MCP audit ledger - every principal's <c>tools/call</c> and <c>resources/read</c>, not only the
        /// reader's own. See the type remarks for why this is not <see cref="View"/>.
        /// </summary>
        public const string ViewAudit = "mcp:view-audit";

        /// <summary>
        /// Register, refresh, enable, disable and remove this site's EXTERNAL MCP servers, and set or clear their API
        /// keys. Never reads a stored key back.
        /// </summary>
        public const string Manage = "mcp:manage";

        /// <summary>Every key, in declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { View, ViewAudit, Manage };

        /// <summary>
        /// Every key with its DEFAULT roles - what the module definition declares and a fresh site seeds. The per-key
        /// <c>PermissionKeys.Declare</c> appends Administrators to every entry, so all three seed to Administrators and
        /// nothing else: an install that publishes nothing by default should not grant anyone a view of it by default
        /// either. An operator who wants a user to reach the catalog grants <see cref="View"/> to a role.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new string[0]),
            (ViewAudit, new string[0]),
            (Manage, new string[0]),
        };
    }
}
