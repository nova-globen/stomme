using System.Collections.Generic;

namespace Stomme.Export
{
    /// <summary>
    /// The data-export module's permission KEYS, decided by the authorization bridge: each key is
    /// a site Permission row <c>(EntityName = "export", EntityId = -1, PermissionName = operation)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in <c>Stomme.Shared</c> because both halves need it: the Client <c>ModuleInfo</c> declares it and
    /// gates its controls with it, the Server's requests, controller and queue job carry it.
    /// </para>
    /// <para>
    /// <b>Three keys, not four.</b> A separate <c>export:read</c> and <c>export:download</c> would always travel
    /// together - nobody may see an export they cannot download, or download one they cannot see - so they collapse
    /// into <see cref="View"/>. <see cref="Manage"/> is what sees OTHER people's exports, and is also the only key
    /// that may turn formula neutralisation off (refutation 11): any holder of <see cref="Create"/> could otherwise
    /// disable the guard for data they do not own.
    /// </para>
    /// <para>
    /// <b>Holding <see cref="Create"/> is never enough to export something.</b> Every <c>IExportSource</c> names a
    /// <c>RequiredPermission</c> of its own for the DATA, checked on top - at request time from the principal, and
    /// again when the job runs, from stored rows. <see cref="Log"/> is that key for the built-in event-log source.
    /// </para>
    /// </remarks>
    public static class ExportPermissions
    {
        /// <summary>The key resource - the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "export";

        /// <summary>Request an export.</summary>
        public const string Create = "export:create";

        /// <summary>See and download one's OWN exports.</summary>
        public const string View = "export:view";

        /// <summary>See, download and cancel ANY export on the site; turn formula neutralisation off.</summary>
        public const string Manage = "export:manage";

        /// <summary>Export the site's event log - the built-in source's data permission.</summary>
        public const string Log = "export:log";

        /// <summary>Every key, in declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { Create, View, Manage, Log };

        /// <summary>
        /// Every key with its DEFAULT roles. <c>PermissionKeys.Declare</c> appends Administrators to each entry, so all
        /// four seed to Administrators only: Wave 1 ships no self-service export, and the event log is an operator's
        /// view of the site.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (Create, new string[0]),
            (View, new string[0]),
            (Manage, new string[0]),
            (Log, new string[0]),
        };
    }
}
