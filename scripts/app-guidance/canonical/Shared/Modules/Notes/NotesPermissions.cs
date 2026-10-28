using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes
{
    /// <summary>The module's permission keys: "resource:operation", lower-case.</summary>
    public static class NotesPermissions
    {
        /// <summary>Read the site's notes.</summary>
        public const string View = "notes:view";

        /// <summary>Write notes, and change or delete your own.</summary>
        public const string Edit = "notes:edit";

        /// <summary>Change or delete anyone's note.</summary>
        public const string Manage = "notes:manage";

        /// <summary>Each key with the roles a site grants it to. PermissionKeys.Declare adds Administrators to every key.</summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { RoleNames.Registered }),
            (Edit, new[] { RoleNames.Registered }),
            (Manage, new[] { RoleNames.Admin }),
        };
    }
}
