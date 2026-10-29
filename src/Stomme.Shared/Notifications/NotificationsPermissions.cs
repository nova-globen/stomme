using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Notifications
{
    /// <summary>
    /// The Notifications module's permission KEYS, decided by the authorization bridge:
    /// each key is a site Permission row <c>(EntityName = "notifications", EntityId = -1, PermissionName = operation)</c>,
    /// seeded from the module definition's <c>PermissionNames</c> and consulted by the mediator's <c>[Requires]</c>, the
    /// controller's <c>[RequiresPermissionKey]</c> and the Client's <c>ModuleBase.IsAuthorizedPermission</c> alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives in <c>Stomme.Shared</c> because BOTH halves need it: the Client <c>ModuleInfo</c> declares it and gates its
    /// controls with it, the Server's requests and controller carry it.
    /// </para>
    /// <para>
    /// <b>There is no single <c>notifications:view</c>; it is SPLIT.</b> One grant over a user's own inbox AND the
    /// whole tenant's send log (and the template preview) would let every user who could read their own
    /// notifications read everyone's. <see cref="ViewOwn"/> is the self-service key every registered user holds;
    /// <see cref="ViewLog"/> is the operator's.
    /// </para>
    /// </remarks>
    public static class NotificationsPermissions
    {
        /// <summary>The key resource - the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "notifications";

        /// <summary>Read the caller's OWN notifications and read/update the caller's OWN preferences.</summary>
        public const string ViewOwn = "notifications:view-own";

        /// <summary>Read the site's send log (every request, its recipients and its attempts).</summary>
        public const string ViewLog = "notifications:view-log";

        /// <summary>Send a test notification and retry a failed request.</summary>
        public const string Send = "notifications:send";

        /// <summary>Configure delivery channels (transports, credentials, enablement, rate limits) and read their configuration.</summary>
        public const string ManageChannels = "notifications:manage-channels";

        /// <summary>Author, read and PREVIEW notification templates.</summary>
        public const string ManageTemplates = "notifications:manage-templates";

        /// <summary>Every key, in declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { ViewOwn, ViewLog, Send, ManageChannels, ManageTemplates };

        /// <summary>
        /// Every key with its DEFAULT roles - what the module definition declares and a fresh site seeds. The per-key
        /// <c>PermissionKeys.Declare</c> appends Administrators to each entry, so <see cref="ViewOwn"/> seeds to Registered
        /// Users AND Administrators and the other four to Administrators only.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (ViewOwn, new[] { RoleNames.Registered }),
            (ViewLog, new string[0]),
            (Send, new string[0]),
            (ManageChannels, new string[0]),
            (ManageTemplates, new string[0]),
        };
    }
}