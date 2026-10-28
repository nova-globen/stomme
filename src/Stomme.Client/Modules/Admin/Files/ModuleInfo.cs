using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Modules.Admin.Files
{
    [PrivateApi("Mark this as private, since it's not very useful in the public docs")]
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "File Management",
            Description = "File Management",
            Version = Constants.Version,
            Categories = "Admin",
            ServerManagerType = "Stomme.Modules.Admin.Files.Manager.FileManager, Stomme.Server"
        };
    }
}
