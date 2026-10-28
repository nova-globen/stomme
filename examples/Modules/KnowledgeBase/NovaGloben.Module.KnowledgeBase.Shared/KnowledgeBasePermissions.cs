using System.Collections.Generic;

namespace NovaGloben.Module.KnowledgeBase
{
    /// <summary>
    /// The module's permission KEYS (D99): <c>resource:action</c>, lower-case, declared once here and used by the
    /// requests' <c>[Requires]</c>, the controller's <c>[RequiresPermissionKey]</c> and the page's
    /// <c>IsAuthorizedPermission</c>. The client's <c>ModuleInfo</c> declares them - with the roles they are granted to on
    /// a new site - through <c>PermissionKeys.Declare</c>; a key no module declares denies everyone, which the
    /// framework's key-declaration gate refuses at build time for in-tree modules.
    /// </summary>
    public static class KnowledgeBasePermissions
    {
        public const string Resource = "knowledge-base";

        /// <summary>Read the published articles and the categories.</summary>
        public const string View = "knowledge-base:view";

        /// <summary>Draft and edit articles, read drafts, create categories.</summary>
        public const string Edit = "knowledge-base:edit";

        /// <summary>Publish an article.</summary>
        public const string Publish = "knowledge-base:publish";

        public static readonly IReadOnlyList<string> All = new[] { View, Edit, Publish };

        /// <summary>The keys and the roles a new site grants them to.</summary>
        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            // A knowledge base is read by the public: every visitor reads the PUBLISHED articles; a draft is only an editor's.
            (View, new[] { "All Users" }),
            (Edit, new[] { "Administrators" }),
            (Publish, new[] { "Administrators" }),
        };
    }
}
