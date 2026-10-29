using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Stomme.Server;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Xunit;

namespace Stomme.Tests
{
    // CreatePagesAsync matches an existing module placement by (PageId, ModuleDefinitionName, Title) —
    // and Title is DEFAULTED from ModuleDefinition.Name by ProcessPageTemplatesAsync, so renaming a module
    // between releases changes the matching key and the additive reconciliation creates a SECOND placement of
    // the same module on the same page instead of recognising the first. Observed live during the idiomatic
    // endgame proof-of-life: two placements of one module rendering one under the other.
    //
    // Title cannot simply leave the key: multiple placements of one module with distinct titles on one page is
    // a legitimate authored shape (DefaultSiteTemplate places six HtmlText modules on Home). The pinned
    // contract is therefore: a title mismatch is treated as the SAME placement only when it is UNAMBIGUOUS —
    // exactly one existing placement of that module on the page AND exactly one declared in the template;
    // anything else stays additive, exactly as before.
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ModulePlacementRenameReconciliationTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string HtmlTextModule = "Stomme.Modules.HtmlText, Stomme.Client";

        [Fact]
        public async Task Renaming_a_modules_placement_title_between_boots_does_not_duplicate_it()
        {
            using var scratch = new PlacementScratchInstall();
            var app = await BuildAsync(scratch);
            try
            {
                await InstallAsync(app);

                await CreatePagesAsync(app, Template("bl75rename", "Original Title"));
                Assert.Equal(new[] { "Original Title" }, await PlacementTitlesAsync(app, "bl75rename"));

                // The rename: the same self-declared template arrives with a new defaulted Title — exactly
                // what a module rename produces on the boot path.
                await CreatePagesAsync(app, Template("bl75rename", "Renamed Title"));

                var titles = await PlacementTitlesAsync(app, "bl75rename");
                Assert.True(titles.Count == 1,
                    $"expected the renamed module to keep its single placement, found {titles.Count}: " +
                    string.Join(", ", titles));
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        [Fact]
        public async Task An_ambiguous_title_mismatch_stays_additive_and_steals_no_placement()
        {
            // The other direction: with MULTIPLE existing placements of the module on the page, a new title
            // is ambiguous — it must stay additive (today's behaviour) rather than silently re-titling or
            // absorbing someone's placement. A fix wide enough to fail this is worse than no fix.
            using var scratch = new PlacementScratchInstall();
            var app = await BuildAsync(scratch);
            try
            {
                await InstallAsync(app);

                await CreatePagesAsync(app, Template("bl75multi", "Left Column", "Right Column"));
                Assert.Equal(2, (await PlacementTitlesAsync(app, "bl75multi")).Count);

                await CreatePagesAsync(app, Template("bl75multi", "Third Title"));

                var titles = await PlacementTitlesAsync(app, "bl75multi");
                Assert.Equal(new[] { "Left Column", "Right Column", "Third Title" },
                    titles.OrderBy(item => item, StringComparer.Ordinal).ToArray());
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        // ---- helpers ----------------------------------------------------------------------------------

        private static Task<WebApplication> BuildAsync(PlacementScratchInstall scratch) =>
            Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

        private static async Task InstallAsync(WebApplication app)
        {
            var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
            var installTask = Task.Run(() => databaseManager.InstallAsync());
            Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "Install timed out.");
            var install = await installTask;
            Assert.True(install.Success, $"Install failed: {install.Message}");
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        private static async Task<T> InTenantAsync<T>(WebApplication app, Func<IServiceProvider, Alias, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return await work(sp, alias);
        }

        private static Task<bool> CreatePagesAsync(WebApplication app, List<PageTemplate> pageTemplates) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var sites = sp.GetRequiredService<ISiteRepository>();
                var site = await sites.GetSiteAsync(alias.SiteId);
                await sites.CreatePagesAsync(site, pageTemplates, alias);
                return true;
            });

        private static Task<List<string>> PlacementTitlesAsync(WebApplication app, string pagePath) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId);
                var page = pages.FirstOrDefault(item => string.Equals(item.Path, pagePath, StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(page);
                var pageModules = await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId);
                return pageModules.Where(item => item.PageId == page!.PageId).Select(item => item.Title).ToList();
            });

        /// <summary>
        /// A self-declared module page template in the exact shape ProcessPageTemplatesAsync hands to
        /// CreatePagesAsync on every boot: Version "*", Update false, AliasName "*", one placement per title.
        /// Fresh objects per call, because CreatePagesAsync mutates the template's Path in place.
        /// </summary>
        private static List<PageTemplate> Template(string path, params string[] titles)
        {
            return new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = path,
                    Path = path,
                    AliasName = "*",
                    Version = "*",
                    Update = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Everyone, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                    PageTemplateModules = titles.Select((title, index) => new PageTemplateModule
                    {
                        ModuleDefinitionName = HtmlTextModule,
                        Title = title,
                        Pane = PaneNames.Default,
                        Order = index + 1,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        }
                    }).ToList()
                }
            };
        }

        /// <summary>A throwaway SQLite install (the AdminPageUpgradeReconciliationTests scaffold, one host).</summary>
        private sealed class PlacementScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;

            public PlacementScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-placement-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = ScratchSqlite.ConnectionString(Path.Combine(ContentRoot, "tenant.db"))
                    },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = TenantHost,
                        ["HostUsername"] = "host",
                        ["HostPassword"] = "H0st!Placement$2026",
                        ["HostEmail"] = "host@placement.test",
                        ["HostName"] = "Host"
                    }
                };
                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
