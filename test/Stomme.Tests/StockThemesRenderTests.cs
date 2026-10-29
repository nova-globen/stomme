#nullable enable
using System.Net;
using System.Threading.Tasks;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The stock themes this host bundles (Aurora public, Slate admin) RENDER on it.
    /// </summary>
    /// <remarks>
    /// Nothing in the gate rendered them before 10.5.0. Their 1.1.2 packs render <c>FluentDesignTheme</c>,
    /// which FluentUI 5 removed, so a host on FluentUI 5 bundling 1.1.2 offered two themes that could not render - and every
    /// suite would have stayed green, because every suite renders Graphite. This installs a site, gives it Aurora as its theme
    /// and Slate as its admin theme, and asserts each renders its own markup. (The test host serves no package's
    /// <c>/_content/</c> files, so the stylesheet is not read here.)
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class StockThemesRenderTests
    {
        private const string Host = "localhost";
        private const string Aurora = "Stomme.Themes.Aurora.Default, Stomme.Theme.Aurora.Stomme";
        private const string Slate = "Stomme.Themes.Slate.Default, Stomme.Theme.Slate.Stomme";

        private readonly ITestOutputHelper _output;

        public StockThemesRenderTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task Aurora_renders_the_public_pages_and_Slate_the_admin_pages()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var sites = sp.GetRequiredService<ISiteRepository>();
                    var site = await sites.GetSiteAsync(alias.SiteId);
                    site.DefaultThemeType = Aurora;
                    site.AdminThemeType = Slate;
                    await sites.UpdateSiteAsync(site);
                    return true;
                });

                using var client = GateDrive.NewClient(app, Host);

                var home = await GateDrive.GetAsync(client, null, Host, "/", _output, "home, anonymous");
                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.Contains("data-theme-marker=\"aurora-public\"", home.Body);

                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var admin = await GateDrive.GetAsync(client, cookies, Host, "/admin", _output, "admin, host");
                Assert.Equal(HttpStatusCode.OK, admin.Status);
                Assert.Contains("data-theme-marker=\"slate-admin\"", admin.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
