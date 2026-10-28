using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;

namespace NovaGloben.Module.KnowledgeBase.Categories
{
    /// <summary>
    /// The SECOND module definition in the package: the knowledge base's categories, placed on the NESTED page
    /// <c>knowledge-base/categories</c>. The host keys a module definition by the namespace of its <c>ModuleInfo</c>, so a
    /// second namespace in the same Client assembly is a second module. It declares no page of its own - the main module's
    /// page templates place it under their parent page, so the parent is always created first.
    /// </summary>
    /// <remarks>
    /// <b>It DOES declare the knowledge base's keys</b> (increment 6, found by clicking it in a browser). The rows are the
    /// site's, but a placement carries them only when its OWN definition declares them - the host attaches declared key rows
    /// to a module instance's permission list from its definition - so without this line every visitor of the nested page was
    /// told "You do not have access", while the API behind it admitted them. No HTTP drive could see it: the page's content is
    /// rendered interactively.
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleDefinitionName = "NovaGloben.Module.KnowledgeBase.Categories, NovaGloben.Module.KnowledgeBase.Client.Stomme";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Knowledge Base Categories",
            Description = "The categories of the site's knowledge base, with their article counts.",
            Categories = "Common",
            Version = "1.0.0",
            Dependencies = "NovaGloben.Module.KnowledgeBase.Shared.Stomme",
            PackageName = "NovaGloben.Module.KnowledgeBase",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "MIT",
            PermissionNames = PermissionKeys.Declare(KnowledgeBasePermissions.Declarations),
        };
    }
}
