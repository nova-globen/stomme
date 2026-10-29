using System.Collections.Generic;
using Stomme.Models;
using Stomme.Themes;
using Stomme.Shared;

namespace [Owner].Theme.[Theme]
{
    public class ThemeInfo : ITheme
    {
        public Stomme.Models.Theme Theme => new Stomme.Models.Theme
        {
            Name = "[Owner] [Theme]",
            Version = "1.0.0",
            // A theme designed for one colour mode says so; empty follows the visitor:
            // ColorMode = ThemeColorModes.Dark,
            PackageName = "[Owner].Theme.[Theme]",
            ThemeSettingsType = "[Owner].Theme.[Theme].ThemeSettings, [Owner].Theme.[Theme].Client.Stomme",
            ContainerSettingsType = "[Owner].Theme.[Theme].ContainerSettings, [Owner].Theme.[Theme].Client.Stomme",
            Resources = new List<Resource>()
            {
		// obtained from https://cdnjs.com/libraries
                new Stylesheet(Constants.BootstrapStylesheetUrl, Constants.BootstrapStylesheetIntegrity, "anonymous"),
                new Stylesheet("_content/[Owner].Theme.[Theme]/Theme.css"),
                new Script(Constants.BootstrapScriptUrl, Constants.BootstrapScriptIntegrity, "anonymous")
            }
        };

    }
}
