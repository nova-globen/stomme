using System.Collections.Generic;

namespace Stomme.Shared
{
    /// <summary>
    /// The installation-wide switches a client component reads before it calls the API they guard. Off - the
    /// default for each - the API answers 404, and a WebAssembly site's browser logged that 404: the help launcher on every
    /// page, a person's data on their profile, the collaboration panel in the pipeline editor and on the dashboards. The
    /// server puts each in the site's settings as <c>{section}:Enabled</c>, the channel D70 made for SAML's and Windows'
    /// switches, and a component asks only when it is not told the switch is off.
    /// </summary>
    public static class HostSwitches
    {
        public const string Help = "Help";
        public const string Portability = "Portability";
        public const string Collaboration = "Collaboration";

        /// <summary>Every switch the server publishes in the site's settings.</summary>
        public static readonly IReadOnlyList<string> Published = new[] { Help, Portability, Collaboration };

        public static string SettingName(string section) => section + ":Enabled";

        /// <summary>
        /// True only when the site says the switch is off. A site that does not say - a server from before this, or a page
        /// rendered without its site - is asked, as before.
        /// </summary>
        public static bool IsOff(Stomme.Models.Site site, string section) =>
            site?.Settings is { } settings && settings.TryGetValue(SettingName(section), out var value) && value != "true";
    }
}
