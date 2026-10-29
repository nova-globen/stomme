using System.Collections.Generic;

namespace Stomme.Formats
{
    /// <summary>
    /// The Formats module's permission KEYS, decided by the authorization bridge: each key is a site
    /// Permission row <c>(EntityName = "formats", EntityId = -1, PermissionName = operation)</c>. In <c>Stomme.Shared</c>
    /// because both halves need it: the Client <c>ModuleInfo</c> declares it and gates its controls with it, the Server's
    /// requests and controller carry it. <see cref="View"/> reads the presets and the site's definitions;
    /// <see cref="Manage"/> saves and deletes the site's own. Both default to Administrators.
    /// </summary>
    public static class FormatPermissions
    {
        public const string Resource = "formats";

        public const string View = "formats:view";

        public const string Manage = "formats:manage";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
        };
    }
}
