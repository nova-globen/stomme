using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The per-site admin-theme split (D87). <see cref="Utilities.ResolveThemeType"/> is the single decision
    /// function both render call sites use — the client router (<c>SiteRouter.ProcessPage</c>) and the SSR head
    /// resource loader (<c>App.razor.GetPageResources</c>). Pinning it here keeps the two in agreement and
    /// guards the load-bearing FALLBACK: an empty admin theme must leave an admin page rendering the site's
    /// default theme, so a stock site is unchanged.
    /// </summary>
    public class AdminThemeResolutionTests
    {
        private const string Default = "Stomme.Themes.GraphiteTheme.Themes.Default, Stomme.Client";
        private const string Admin = "Stomme.Themes.Slate.Default, Stomme.Theme.Slate.Stomme";
        private const string Explicit = "Some.Explicit.PageTheme, Some.Assembly";

        [Theory]
        [InlineData("admin")]
        [InlineData("admin/site")]
        [InlineData("admin/pages")]
        public void An_admin_page_with_an_admin_theme_set_renders_the_admin_theme(string path)
        {
            Assert.Equal(Admin, Utilities.ResolveThemeType(pageThemeType: "", pagePath: path, defaultThemeType: Default, adminThemeType: Admin));
        }

        [Theory]
        [InlineData("")]
        [InlineData("home")]
        [InlineData("about")]
        [InlineData("administrators")] // NOT an admin path — must not be treated as one
        public void A_public_page_renders_the_default_theme(string path)
        {
            Assert.Equal(Default, Utilities.ResolveThemeType(pageThemeType: "", pagePath: path, defaultThemeType: Default, adminThemeType: Admin));
        }

        // The fallback that keeps a stock site unchanged: no admin theme configured -> admin pages use the
        // default theme, exactly as they did before the field existed.
        [Theory]
        [InlineData("admin")]
        [InlineData("admin/site")]
        public void An_admin_page_with_no_admin_theme_falls_back_to_the_default_theme(string path)
        {
            Assert.Equal(Default, Utilities.ResolveThemeType(pageThemeType: "", pagePath: path, defaultThemeType: Default, adminThemeType: ""));
            Assert.Equal(Default, Utilities.ResolveThemeType(pageThemeType: "", pagePath: path, defaultThemeType: Default, adminThemeType: null));
        }

        // An explicit per-page theme always wins over both site defaults — admin path or not.
        [Theory]
        [InlineData("home")]
        [InlineData("admin/site")]
        public void An_explicit_page_theme_always_wins(string path)
        {
            Assert.Equal(Explicit, Utilities.ResolveThemeType(pageThemeType: Explicit, pagePath: path, defaultThemeType: Default, adminThemeType: Admin));
        }

        [Theory]
        [InlineData("admin", true)]
        [InlineData("admin/site", true)]
        [InlineData("admin/pages/edit", true)]
        [InlineData("administrators", false)]
        [InlineData("home", false)]
        [InlineData("", false)]
        public void IsAdminPath_matches_admin_and_admin_slash_only(string path, bool expected)
        {
            Assert.Equal(expected, Utilities.IsAdminPath(path));
        }
    }
}
