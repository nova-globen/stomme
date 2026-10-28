using System.Collections.Generic;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Themes.StommeTheme
{
    [PrivateApi("Mark Build-In Theme-Info classes as private, since it's not very useful in the public docs")]
    public class ThemeInfo : ITheme
    {
        public Theme Theme => new Theme
        {
            Name = "Stomme Theme",
            Version = "1.0.0",
            ThemeSettingsType = "Stomme.Themes.StommeTheme.ThemeSettings, Stomme.Client",
            ContainerSettingsType = "Stomme.Themes.StommeTheme.ContainerSettings, Stomme.Client",
            Resources = new List<Resource>()
            {
                // Stomme ships the --db-* design system as the default theme's CSS (D11).
                // FluentUI Blazor supplies the component chrome; no Bootstrap/Bootswatch CDN.
                new Stylesheet("~/Theme.css")
            }
        };
    }
}
