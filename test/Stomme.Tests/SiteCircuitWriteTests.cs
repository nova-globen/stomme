#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Repository;
using Stomme.Repository.Stores;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-193 - a second save of the site from ONE Interactive Server circuit.
    /// </summary>
    /// <remarks>
    /// Found clicking BL-192's pages (2026-10-02): User Management's Settings tab saved once ("Settings Saved Successfully")
    /// and failed the second time in the same circuit ("Error Saving Settings"; the event log: EF Core's "SiteAggregate
    /// cannot be tracked because another instance with the same key value is already being tracked"), reproduced twice, in
    /// 10.4.1 too. A circuit is one DI scope for as long as the page is open, and the site repository wrote through ONE
    /// store whose context kept the first save's aggregate - the BL-11 identity-map class across two calls rather than
    /// within one method, so <c>PerWriteStoreInvariantTests</c> cannot see it. Reproduced here as what a circuit is: two
    /// saves through one scope, each read back through a scope of its own. The site's server twin (<c>SiteService</c>) is
    /// the only server-side service that writes a framework seam row from a circuit; every other admin page saves over HTTP,
    /// one request scope per save. EF only - RavenDB opens a session per write.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SiteCircuitWriteTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public SiteCircuitWriteTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_site_saves_twice_from_one_circuit()
        {
            var store = new SqliteGateStore();
            using var scratch = new ScratchGateInstall(store, Host);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                await GateDrive.InTenantAsync(app, Host, async (circuit, alias) =>
                {
                    var sites = circuit.GetRequiredService<ISiteRepository>();
                    foreach (var name in new[] { "First save", "Second save" })
                    {
                        var site = await sites.GetSiteAsync(alias.SiteId);
                        site.Name = name;
                        await sites.UpdateSiteAsync(site);

                        // The row itself, through a query session of a fresh scope - the repository's read is cached, and the
                        // cached object is the very one this "page" edited.
                        var saved = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                        {
                            var session = fresh.GetRequiredService<IFrameworkStoreSessionFactory>().OpenQuerySession();
                            try
                            {
                                return session.Query<SiteAggregate>().ToList().Single(item => item.Id == alias.SiteId).Name;
                            }
                            finally
                            {
                                await session.DisposeAsync();
                            }
                        });
                        Assert.Equal(name, saved);
                    }

                    return true;
                });
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task An_html_text_saves_past_its_version_limit_from_one_circuit()
        {
            // The same shape in the HtmlText module's repository: its store is held for the scope, so the rows a circuit
            // ADDED stay tracked - and once the saves pass the version limit (five by default), the pruning deletes one of
            // them through an instance read by a query session, a second instance with the same key.
            var store = new SqliteGateStore();
            using var scratch = new ScratchGateInstall(store, Host);
            var app = await GateDrive.InstallAsync(scratch, _output, configure: null!, start: false);
            try
            {
                var moduleId = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IModuleRepository>().GetModulesAsync(alias.SiteId))
                        .First(module => module.ModuleDefinitionName.Contains("HtmlText", StringComparison.Ordinal)).ModuleId);

                await GateDrive.InTenantAsync(app, Host, async (circuit, _) =>
                {
                    var texts = circuit.GetRequiredService<Stomme.Modules.HtmlText.Repository.IHtmlTextRepository>();
                    for (var save = 1; save <= 7; save++)
                    {
                        await texts.AddHtmlTextAsync(new Stomme.Modules.HtmlText.Models.HtmlText { ModuleId = moduleId, Content = $"<p>save {save}</p>" });
                    }

                    return true;
                });

                var latest = await GateDrive.InTenantAsync(app, Host, async (fresh, _) =>
                    (await fresh.GetRequiredService<Stomme.Modules.HtmlText.Repository.IHtmlTextRepository>().GetHtmlTextAsync(moduleId)).Content);
                Assert.Equal("<p>save 7</p>", latest);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
