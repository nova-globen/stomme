using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;

namespace [Owner].Module.[Module]
{
    /// <summary>
    /// The module definition the host discovers by convention. It declares the module's permission KEYS with the roles a new
    /// site grants them to, names the Shared assembly a WebAssembly site must also load, and names the server manager
    /// the host asks for search content. Add <c>PageTemplates</c> to place the module on a page of every site; without them an
    /// administrator places it from a page's edit mode.
    /// </summary>
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "[Module]",
            Description = "[Description]",
            Version = "1.0.0",
            Dependencies = "[Owner].Module.[Module].Shared.Stomme",
            PackageName = "[Owner].Module.[Module]",
            ServerManagerType = "[ServerManagerType]",
            PermissionNames = PermissionKeys.Declare([Module]Permissions.Declarations),
        };
    }
}
