using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Modules.Admin.SearchResults
{
    [PrivateApi("Mark this as private, since it's not very useful in the public docs")]
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Search Results",
            Description = "Search Results",
            Categories = "Admin",
            Version = Constants.Version,
            SettingsType = "Stomme.Modules.Admin.SearchResults.Settings, Stomme.Client"
        };
    }
}
