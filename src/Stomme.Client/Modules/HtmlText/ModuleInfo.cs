using System.Collections.Generic;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Modules.HtmlText
{
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "HtmlText",
            Description = "Renders HTML or Text Content",
            Version = "1.0.1",
            ServerManagerType = "Stomme.Modules.HtmlText.Manager.HtmlTextManager, Stomme.Server",
            ReleaseVersions = "1.0.0,1.0.1",
            SettingsType = "Stomme.Modules.HtmlText.Settings, Stomme.Client",
            ConfigSettingsType = "Stomme.Modules.HtmlText.Config, Stomme.Client",
            Resources = new List<Resource>()
            {
                new Stylesheet("~/Module.css")
            }
        };
    }
}
