using System.Collections.Generic;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Themes;

namespace Stomme.Themes.Aurora
{
    public class ThemeInfo : ITheme
    {
        public Stomme.Models.Theme Theme => new Stomme.Models.Theme
        {
            Name = "Aurora (Public)",
            Version = "1.0.0",
            PackageName = "Stomme.Theme.Aurora",
            // No settings controls — the theme reads no per-site settings, so the settings type strings are
            // left empty (SiteRouter/App.razor skip the settings-resource lookup when they are empty).
            ThemeSettingsType = "",
            ContainerSettingsType = "",
            Resources = new List<Resource>()
            {
                // Installed by InstallationManager to {webRoot}/_content/Stomme.Theme.Aurora/Theme.css
                // (staticwebassets/ -> _content/<nuspec id>/), served at that URL. Not a "~/" theme-path asset.
                new Stylesheet("_content/Stomme.Theme.Aurora/Theme.css")
            }
        };
    }
}
