using System.Collections.Generic;
using System.Linq;

namespace Stomme.Shared
{
    /// <summary>
    /// The colour modes a theme can declare in <c>Theme.ColorMode</c>. Empty, the default, follows the visitor: their
    /// choice from the toggle, else their system's. <see cref="Dark"/> or <see cref="Light"/> fixes every page the theme renders to
    /// that mode - the host applies it before the first paint, hands it to FluentUI, and the toggle neither shows nor changes it.
    /// </summary>
    public static class ThemeColorModes
    {
        public const string FollowVisitor = "";
        public const string Dark = "dark";
        public const string Light = "light";

        /// <summary>The declared mode as the host applies it: <see cref="Dark"/>, <see cref="Light"/>, or empty for anything else.</summary>
        public static string Normalize(string colorMode)
        {
            var mode = colorMode?.Trim().ToLowerInvariant();
            return mode == Dark || mode == Light ? mode : FollowVisitor;
        }

        /// <summary>The declared mode of the theme that renders <paramref name="themeType"/>, normalized; empty when none of the themes does.</summary>
        public static string Of(IEnumerable<Stomme.Models.Theme> themes, string themeType) =>
            Normalize(themes?.FirstOrDefault(theme => theme.Themes?.Any(control => control.TypeName == themeType) == true)?.ColorMode);
    }
}
