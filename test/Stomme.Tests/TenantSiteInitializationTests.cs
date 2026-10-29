#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The boot path brings each tenant's sites up to date in THAT tenant's database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect, measured on UAT (2026-10-02).</b> The admin dashboard showed every module's admin page three times.
    /// UAT has a second tenant whose alias points at site 1 of its own database; on each boot that reconciled pages, the
    /// second tenant's pass read its own database, found the pages missing and created them - in the FIRST tenant's
    /// database. <c>DatabaseManager.InitializeSitesAsync</c> walked every tenant and site inside ONE DI scope, so the page
    /// store held by the scope's <c>PageRepository</c> was bound to the first tenant's connection by its first write,
    /// while every read went through a fresh session for the tenant being processed.
    /// </para>
    /// <para>
    /// Every other suite installs ONE tenant, the shape in which the shared scope is harmless. This drives the shape the
    /// defect needs: two tenants on two databases, a release whose admin template offers a page neither has yet (help,
    /// switched on between the two boots), and the boot path itself.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class TenantSiteInitializationTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string SecondHost = "second.localhost";
        private const string NewAdminPagePath = "admin/help";

        [Fact]
        public async Task A_boot_writes_each_tenants_new_admin_page_into_that_tenants_own_database()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            int firstBefore;
            int secondBefore;

            // ---- the older release: two tenants on two databases, help off ----
            var older = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(older);
            try
            {
                var provisioned = await InScopeAsync(older, sp => sp.GetRequiredService<IDatabaseManager>().InstallAsync(new InstallConfig
                {
                    TenantName = "Second",
                    IsNewTenant = true,
                    DatabaseType = new SqliteGateStore().DatabaseType,
                    ConnectionString = "Data Source=" + Path.Combine(scratch.ContentRoot, "second-tenant.db"),
                    Aliases = SecondHost,
                    SiteName = "Second",
                    HostUsername = GateDrive.HostUsername,
                    HostPassword = GateDrive.HostPassword,
                    HostEmail = GateDrive.HostEmail,
                    HostName = "Host",
                    SiteTemplate = Constants.DefaultSiteTemplate,
                    DefaultTheme = Constants.DefaultTheme,
                    DefaultContainer = Constants.DefaultContainer,
                    DefaultAdminContainer = string.Empty,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                }));
                Assert.True(provisioned.Success, "the second tenant was not provisioned: " + provisioned.Message);

                var first = await GateDrive.AliasAsync(older, Host);
                var second = await GateDrive.AliasAsync(older, SecondHost);
                Assert.NotEqual(first.TenantId, second.TenantId);
                output.WriteLine($"[tenants] first={first.TenantId} (site {first.SiteId}), second={second.TenantId} (site {second.SiteId})");

                firstBefore = (await PathsAsync(older, Host)).Length;
                secondBefore = (await PathsAsync(older, SecondHost)).Length;
                Assert.DoesNotContain(NewAdminPagePath, await PathsAsync(older, Host));
                Assert.DoesNotContain(NewAdminPagePath, await PathsAsync(older, SecondHost));
                output.WriteLine($"[before] first holds {firstBefore} pages, second holds {secondBefore}");
            }
            finally
            {
                await GateDrive.ShutdownAsync(older);
            }

            SwitchHelpOn(scratch.ContentRoot);

            // ---- the newer release boots: its admin template offers admin/help to every site ----
            var newer = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                var firstAfter = await PathsAsync(newer, Host);
                var secondAfter = await PathsAsync(newer, SecondHost);
                output.WriteLine($"[after] first holds {firstAfter.Length} pages ({firstAfter.Count(path => path == NewAdminPagePath)} {NewAdminPagePath}), " +
                    $"second holds {secondAfter.Length} ({secondAfter.Count(path => path == NewAdminPagePath)} {NewAdminPagePath})");

                // Each tenant gains the one page, in its own database - nothing of the second tenant lands in the first.
                Assert.Single(firstAfter, path => path == NewAdminPagePath);
                Assert.Single(secondAfter, path => path == NewAdminPagePath);
                Assert.Equal(firstBefore + 1, firstAfter.Length);
                Assert.Equal(secondBefore + 1, secondAfter.Length);

                // Every later boot finds the page where it put it, and adds nothing anywhere.
                var again = await Program.InstallAsync(newer);
                Assert.True(again.Success, "the second boot pass failed: " + again.Message);
                Assert.Equal(firstAfter, await PathsAsync(newer, Host));
                Assert.Equal(secondAfter, await PathsAsync(newer, SecondHost));
            }
            finally
            {
                await GateDrive.ShutdownAsync(newer);
            }
        }

        private static async Task<T> InScopeAsync<T>(WebApplication app, Func<IServiceProvider, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            return await work(scope.ServiceProvider);
        }

        /// <summary>Every page path of the site <paramref name="host"/> resolves to, read in that site's tenant, sorted.</summary>
        private static Task<string[]> PathsAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
                (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId))
                    .Select(page => (page.Path ?? string.Empty).ToLowerInvariant())
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray());

        /// <summary>
        /// Switches help on in the install's override file, re-reading it first: the install and the second tenant's
        /// provisioning wrote to it (the installation id, the second tenant's connection string).
        /// </summary>
        private static void SwitchHelpOn(string contentRoot)
        {
            var file = Path.Combine(contentRoot, "appsettings.override.json");
            var root = JsonNode.Parse(System.IO.File.ReadAllText(file))?.AsObject() ?? new JsonObject();
            root["Help"] = new JsonObject { ["Enabled"] = true };
            System.IO.File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
