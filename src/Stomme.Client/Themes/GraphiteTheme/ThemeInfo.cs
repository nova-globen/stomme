using System.Collections.Generic;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Themes.GraphiteTheme
{
    [PrivateApi("Mark Build-In Theme-Info classes as private, since it's not very useful in the public docs")]
    public class ThemeInfo : ITheme
    {
        public Theme Theme => new Theme
        {
            Name = "Graphite",
            Version = "1.0.0",
            ThemeSettingsType = "Stomme.Themes.GraphiteTheme.ThemeSettings, Stomme.Client",
            ContainerSettingsType = "Stomme.Themes.GraphiteTheme.ContainerSettings, Stomme.Client",
            Resources = new List<Resource>()
            {
                // Graphite ships its OWN accent-token block plus a near-monochrome surface skin as this
                // theme's CSS (D60). The `--db-*` component layer + base still come from css/app.css; this
                // file only overrides the accent tokens, the surface ramp, and the theme's own shell. Owning
                // its accent block is what makes the amber bleed structurally impossible (D31 loading order).
                new Stylesheet("~/Theme.css")
            }
        };
    }
}
