using System.Collections.Generic;
using Stomme.Shared;

namespace Stomme.Help
{
    /// <summary>
    /// Contextual help's permission KEYS (BL-84 / D103 item 8), decided by the authorization bridge (D99).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The per-key <c>Declare</c> overload is not a convenience here, it is the whole reason these three keys are
    /// safe</b> (D103 refutation 15). The UNIFORM overload applies one role segment to EVERY key and always appends
    /// Administrators - so declaring these with <c>Everyone</c> would seed <c>help:manage</c> to All Users, and
    /// <c>UserSecurity.IsAuthorized</c> admits All Users at userId -1. An ANONYMOUS visitor would author and manage
    /// help. A gate asserts an anonymous principal holds <see cref="View"/> and nothing else.
    /// </para>
    /// <para>
    /// <b><see cref="View"/> really is for Everyone, and that is deliberate.</b> Help on a public page is for the
    /// reader of that page, who may not have an account. The alternative - Registered Users - was considered and
    /// rejected in D103's defaults: it would make the launcher render nothing for exactly the visitor most likely to
    /// need it. Nothing gated on <see cref="View"/> returns anything but PUBLISHED topics of the current site.
    /// </para>
    /// </remarks>
    public static class HelpPermissions
    {
        /// <summary>The key resource - the Permission row's <c>EntityName</c>.</summary>
        public const string Resource = "help";

        /// <summary>Read a PUBLISHED topic of this site. Held by everyone, including an anonymous reader.</summary>
        public const string View = "help:view";

        /// <summary>Write and publish topics, and preview unsaved Markdown through the server-side renderer.</summary>
        public const string Author = "help:author";

        /// <summary>List drafts, delete a topic, and reset an authored topic back to its contributed seed.</summary>
        public const string Manage = "help:manage";

        /// <summary>Every key, in declaration order.</summary>
        public static readonly IReadOnlyList<string> All = new[] { View, Author, Manage };

        /// <summary>
        /// Every key with its DEFAULT roles. <c>PermissionKeys.Declare</c> appends Administrators to each entry, so
        /// <see cref="View"/> seeds to Everyone AND Administrators, and the other two to Administrators alone.
        /// </summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { RoleNames.Everyone }),
            (Author, new string[0]),
            (Manage, new string[0]),
        };
    }
}
