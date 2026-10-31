#nullable enable
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A static site may leave <c>blazor.web.js</c> - and the FluentUI and Stomme JS modules its initializers load, about
    /// 200 KB - off a page that needs none of it (BL-269, the website strips them by rewriting responses today). Opt-in by the
    /// site setting <c>OmitBlazorScriptOnStaticPages</c>; only for a visitor who is not signed in, on a page with no
    /// interactive module. Driven on a Static site: off, the page loads the script; on, an anonymous visitor's page does not;
    /// on, the signed-in host's page still does.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class StaticPageScriptsTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task A_static_site_leaves_blazor_web_js_off_an_anonymous_page_only_when_it_opts_in()
        {
            var settings = new JsonObject { ["RenderMode"] = "Static", ["Runtime"] = "Server", ["Prerender"] = "true" };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var before = await GateDrive.GetAsync(client, null, Host, "/", output, "a static home page, the setting absent");
                Assert.Equal(HttpStatusCode.OK, before.Status);
                Assert.Contains("_framework/blazor.web.js", before.Body);

                await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    await sp.GetRequiredService<ISettingRepository>().AddSettingAsync(new Setting { EntityName = EntityNames.Site, EntityId = alias.SiteId, SettingName = "OmitBlazorScriptOnStaticPages", SettingValue = "true", IsPrivate = false });
                    await sp.GetRequiredService<Stomme.Infrastructure.ISyncManager>().AddSyncEventAsync(alias, EntityNames.Site, alias.SiteId, SyncEventActions.Refresh);
                    return 0;
                });

                var after = await GateDrive.GetAsync(client, null, Host, "/", output, "a static home page, the setting on");
                Assert.Equal(HttpStatusCode.OK, after.Status);
                Assert.DoesNotContain("_framework/blazor.web.js", after.Body);
                Assert.DoesNotContain("js/reload.js", after.Body);
                // BL-278: nothing calls these without Blazor's interop, so they go with it.
                Assert.DoesNotContain("js/app.js", after.Body);
                Assert.DoesNotContain("js/interop.js", after.Body);
                Assert.DoesNotContain("js/loadjs.min.js", after.Body);

                var cookies = await GateDrive.LoginAsync(app, client, Host, UserNames.Host, GateDrive.HostPassword, output);
                var signedIn = await GateDrive.GetAsync(client, cookies, Host, "/", output, "the same page, signed in");
                Assert.Equal(HttpStatusCode.OK, signedIn.Status);
                Assert.Contains("_framework/blazor.web.js", signedIn.Body);
                Assert.Contains("js/interop.js", signedIn.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
