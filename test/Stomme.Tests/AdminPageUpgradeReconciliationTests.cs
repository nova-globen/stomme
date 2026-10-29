using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Server;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// An admin page added by a NEWER framework release must appear on an ALREADY-INSTALLED site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this pins, measured on the UAT deployment.</b> <c>AdminSiteTemplate</c> is consulted only by
    /// the install path, so its pages are created exactly once, at install. Ship a release that adds an admin page
    /// and every existing site is missing it forever: the module itself registers by assembly scan (it is even
    /// selectable when adding a module to a page by hand), but its page never exists, so the feature is invisible.
    /// On UAT the module definition <c>Stomme.Modules.Admin.OpenIddict</c> was present while
    /// <c>select count(*) from page where path='admin/openiddict'</c> returned 0.
    /// </para>
    /// <para>
    /// The in-tree suites could not see this: every one of them installs FRESH, which is the one path that does
    /// seed admin pages. This test deliberately drives the UPGRADE shape instead — install, remove a page the way
    /// an older release would have left it, then run the boot-path reconciliation.
    /// </para>
    /// <para>
    /// The second case is the one that keeps the fix honest: reconciliation must be <b>purely additive</b>. It
    /// mutates an existing admin page and asserts the mutation SURVIVES, because <c>CreatePagesAsync</c> re-applies
    /// page-module properties regardless of <c>PageTemplate.Update</c> — so a reconciliation that simply replayed
    /// the whole template would silently revert an operator's customisation on every boot.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class AdminPageUpgradeReconciliationTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Upgrade$2026";
        private const string HostEmail = "host@upgrade.test";

        // The admin page the IdP release adds — the concrete case that exposed the defect.
        private const string NewAdminPagePath = "admin/openiddict";

        // A stock admin page every install has; used as the do-not-touch control.
        private const string ExistingAdminPagePath = "admin/users";

        // The admin page the MCP release adds, behind its own switches.
        private const string McpAdminPagePath = "admin/mcp";
        private const string HelpAdminPagePath = "admin/help";

        private readonly ITestOutputHelper _output;

        public AdminPageUpgradeReconciliationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task An_admin_page_from_a_newer_release_is_added_to_an_already_installed_site()
        {
            // The two phases share ONE database: phase 1 is the site as an older release left it, phase 2 is the
            // same site booted on a release whose admin template offers a page it has never seen.
            using var scratch = new ScratchInstall();

            // ---- Phase 1: install on a release WITHOUT the page ------------------------------------------
            scratch.SetOpenIddictEnabled(false);
            var older = await BuildAsync(scratch);
            try
            {
                await InstallAsync(older);

                // The page genuinely never existed — not soft-deleted, which is a different thing entirely and
                // would leave the row occupying (SiteId, Path).
                Assert.Null(await FindPageAsync(older, NewAdminPagePath));
                _output.WriteLine($"phase 1: installed WITHOUT '{NewAdminPagePath}' — the pre-release shape");
            }
            finally
            {
                await SafeShutdownAsync(older);
            }

            // ---- Phase 2: same database, newer release (its admin template now offers the page) ----------
            scratch.SetOpenIddictEnabled(true);
            var newer = await BuildAsync(scratch);
            try
            {
                // Before reconciling: the module registers by assembly scan, which is exactly why the missing
                // page is invisible rather than obviously broken.
                Assert.NotNull(await FindAdminModuleDefinitionAsync(newer, "OpenIddict"));
                Assert.Null(await FindPageAsync(newer, NewAdminPagePath));

                await ReconcileAsync(newer);

                var page = await FindPageAsync(newer, NewAdminPagePath);
                Assert.NotNull(page);
                _output.WriteLine($"phase 2: reconciled — '{page!.Path}' now exists (PageId {page.PageId})");

                Assert.Contains(page.PermissionList,
                    p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Host && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Admin && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);

                var modules = await PageModuleNamesAsync(newer, page.PageId);
                _output.WriteLine($"modules on the page: {string.Join(", ", modules)}");
                Assert.Contains(modules, name => name != null && name.Contains("Admin.OpenIddict", StringComparison.Ordinal));

                // Idempotence: a second boot adds nothing and duplicates nothing.
                var before = (await AllPagePathsAsync(newer)).OrderBy(p => p).ToList();
                await ReconcileAsync(newer);
                var after = (await AllPagePathsAsync(newer)).OrderBy(p => p).ToList();
                Assert.Equal(before, after);
                Assert.Single(before, p => p == NewAdminPagePath);
            }
            finally
            {
                await SafeShutdownAsync(newer);
            }
        }

        [Fact]
        public async Task The_MCP_admin_page_reaches_a_site_installed_before_MCP_existed()
        {
            // BL-80 / D101 item 7. The MCP page is declared in AdminSiteTemplate behind Mcp:Enabled /
            // Mcp:Server:Enabled, exactly as the OpenIddict and Data Export pages are behind theirs - so an install
            // that predates the feature, or has it switched off, must gain the page the moment a half is turned on.
            // Nothing a FRESH install asserts can see this: it is a statement about a site that already exists.
            using var scratch = new ScratchInstall();

            // ---- Phase 1: a release that has never heard of MCP -------------------------------------------
            scratch.SetOpenIddictEnabled(false);
            scratch.SetMcpEnabled(null);
            var older = await BuildAsync(scratch);
            try
            {
                await InstallAsync(older);
                Assert.Null(await FindPageAsync(older, McpAdminPagePath));
                _output.WriteLine($"phase 1: installed WITHOUT '{McpAdminPagePath}'");
            }
            finally
            {
                await SafeShutdownAsync(older);
            }

            // ---- Phase 2: the same database, MCP switched on ----------------------------------------------
            scratch.SetOpenIddictEnabled(false);
            scratch.SetMcpEnabled(true);
            var newer = await BuildAsync(scratch);
            try
            {
                // The module registers by assembly SCAN either way, which is exactly why a missing page is
                // invisible rather than obviously broken: it is selectable when adding a module to a page by hand,
                // and the feature simply is not there.
                // NOT FindAdminModuleDefinitionAsync: that helper looks for Stomme.Modules.Admin.{name}, which is
                // the framework's built-in Admin controls. MCP is an ordinary MODULE (Stomme.Modules.Mcp), and the
                // distinction is the point - it is discovered by the same assembly scan either way.
                Assert.NotNull(await FindModuleDefinitionAsync(newer, "Stomme.Modules.Mcp,"));
                Assert.Null(await FindPageAsync(newer, McpAdminPagePath));

                await ReconcileAsync(newer);

                var page = await FindPageAsync(newer, McpAdminPagePath);
                Assert.NotNull(page);
                _output.WriteLine($"phase 2: reconciled - '{page!.Path}' now exists (PageId {page.PageId})");

                // Administrators, because all three MCP keys default there; what each control shows is decided per
                // key, and again by the server on every request.
                Assert.Contains(page.PermissionList,
                    p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Admin && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);

                var modules = await PageModuleNamesAsync(newer, page.PageId);
                _output.WriteLine($"modules on the page: {string.Join(", ", modules)}");
                Assert.Contains(modules, name => name != null && name.Contains("Modules.Mcp", StringComparison.Ordinal));

                // Idempotence: a second boot adds nothing and duplicates nothing.
                var before = (await AllPagePathsAsync(newer)).OrderBy(path => path).ToList();
                await ReconcileAsync(newer);
                var after = (await AllPagePathsAsync(newer)).OrderBy(path => path).ToList();
                Assert.Equal(before, after);
                Assert.Single(before, path => path == McpAdminPagePath);
            }
            finally
            {
                await SafeShutdownAsync(newer);
            }
        }

        [Fact]
        public async Task The_help_admin_page_reaches_a_site_installed_while_help_was_OFF()
        {
            // Admin/help is declared in AdminSiteTemplate behind Help:Enabled (default FALSE), the
            // Data Export and MCP posture - so a site installed with help off must gain the page the moment it is
            // switched on, and a fresh install that never switches it on must not have it.
            using var scratch = new ScratchInstall();

            // ---- Phase 1: help absent (what every install before BL-84 looks like) ------------------------
            scratch.SetOpenIddictEnabled(false);
            scratch.SetHelpEnabled(null);
            var older = await BuildAsync(scratch);
            try
            {
                await InstallAsync(older);
                Assert.Null(await FindPageAsync(older, HelpAdminPagePath));
            }
            finally
            {
                await SafeShutdownAsync(older);
            }

            // ---- Phase 2: the same database, help switched on ---------------------------------------------
            scratch.SetOpenIddictEnabled(false);
            scratch.SetHelpEnabled(true);
            var newer = await BuildAsync(scratch);
            try
            {
                // Discovered by the assembly scan either way - which is why a missing page is invisible, not broken.
                Assert.NotNull(await FindModuleDefinitionAsync(newer, "Stomme.Modules.ContextualHelp,"));
                Assert.Null(await FindPageAsync(newer, HelpAdminPagePath));

                await ReconcileAsync(newer);

                var page = await FindPageAsync(newer, HelpAdminPagePath);
                Assert.NotNull(page);
                _output.WriteLine($"phase 2: reconciled - '{page!.Path}' now exists (PageId {page.PageId})");

                // Administrators: help:author and help:manage default there. NOT Everyone - help:view is Everyone's, but
                // the ADMIN page is where topics are written, and a reader never needs it.
                Assert.Contains(page.PermissionList,
                    p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Admin && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);

                var modules = await PageModuleNamesAsync(newer, page.PageId);
                Assert.Contains(modules, name => name != null && name.Contains("Modules.ContextualHelp", StringComparison.Ordinal));

                // Idempotence: a second boot adds nothing and duplicates nothing.
                var before = (await AllPagePathsAsync(newer)).OrderBy(path => path).ToList();
                await ReconcileAsync(newer);
                Assert.Equal(before, (await AllPagePathsAsync(newer)).OrderBy(path => path).ToList());
                Assert.Single(before, path => path == HelpAdminPagePath);
            }
            finally
            {
                await SafeShutdownAsync(newer);
            }
        }

        [Fact]
        public async Task Reconciliation_is_additive_and_does_not_overwrite_an_existing_admin_page()
        {
            using var scratch = new ScratchInstall();

            const string customTitle = "Renamed By The Operator";
            const int customOrder = 987;

            // ---- Phase 1: install on the older release, then customise a stock admin page ----------------
            scratch.SetOpenIddictEnabled(false);
            var older = await BuildAsync(scratch);
            try
            {
                await InstallAsync(older);

                var page = await FindPageAsync(older, ExistingAdminPagePath);
                Assert.NotNull(page);
                page!.Title = customTitle;
                page.Order = customOrder;
                await UpdatePageAsync(older, page);
                _output.WriteLine($"phase 1: '{ExistingAdminPagePath}' customised (Title='{customTitle}', Order={customOrder})");
            }
            finally
            {
                await SafeShutdownAsync(older);
            }

            // ---- Phase 2: newer release reconciles — it MUST add one page and touch nothing else ---------
            scratch.SetOpenIddictEnabled(true);
            var newer = await BuildAsync(scratch);
            try
            {
                await ReconcileAsync(newer);

                // It genuinely had work to do, so this cannot pass vacuously by doing nothing at all.
                Assert.NotNull(await FindPageAsync(newer, NewAdminPagePath));

                var after = await FindPageAsync(newer, ExistingAdminPagePath);
                Assert.NotNull(after);
                _output.WriteLine($"phase 2: '{ExistingAdminPagePath}' Title='{after!.Title}' Order={after.Order}");
                Assert.Equal(customTitle, after.Title);
                Assert.Equal(customOrder, after.Order);
            }
            finally
            {
                await SafeShutdownAsync(newer);
            }
        }

        // ---- D105: admin/licensing is gated on Deployment:Mode=OnPrem ------------------------------------------
        //
        // The reverse edge is deliberately NOT a fact here: flipping OnPrem OFF never removes the page, because the
        // reconciliation is additive by construction (the second fact above pins that). A site switched back to Hosted
        // keeps an 'admin/licensing' page whose control and api/Licence both answer "licensing is not enabled" (404) -
        // refutation 5 of section J.BL-2 accepts that edge rather than adding a removal path.

        private const string DataExportAdminPagePath = "admin/dataexport";

        [Fact]
        public async Task The_data_export_page_and_folder_reach_an_EXISTING_site_once_when_the_feature_is_switched_on()
        {
            // BL-85 (D108 items 7 and 11). Every other export suite installs FRESH with the feature already on, so none of
            // them can see the upgrade shape: a site that was installed without the feature and has it switched on later.
            using var scratch = new ScratchInstall();

            // ---- Phase 1: installed with DataExport:Enabled absent - a stock install gains neither --------------
            scratch.SetDataExportEnabled(null);
            var stock = await BuildAsync(scratch);
            try
            {
                await InstallAsync(stock);
                Assert.Null(await FindPageAsync(stock, DataExportAdminPagePath));
                Assert.Null(await ExportsFolderAsync(stock));

                // The reconciler runs on every boot and must do NOTHING while the feature is off.
                await ReconcileExportFolderAsync(stock);
                Assert.Null(await ExportsFolderAsync(stock));
                _output.WriteLine("phase 1: stock install - no export page, no Exports folder");
            }
            finally
            {
                await SafeShutdownAsync(stock);
            }

            // ---- Phase 2: the SAME database, the feature switched on -----------------------------------------------
            scratch.SetDataExportEnabled(true);
            var enabled = await BuildAsync(scratch);
            try
            {
                Assert.Null(await FindPageAsync(enabled, DataExportAdminPagePath));

                await ReconcileAsync(enabled);
                await ReconcileExportFolderAsync(enabled);

                var page = await FindPageAsync(enabled, DataExportAdminPagePath);
                Assert.NotNull(page);
                Assert.Contains(page!.PermissionList, p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Admin && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Registered && p.IsAuthorized);

                var modules = await PageModuleNamesAsync(enabled, page.PageId);
                _output.WriteLine($"phase 2: '{page.Path}' exists (PageId {page.PageId}) with {string.Join(", ", modules)}");
                Assert.Contains(modules, name => name != null && name.Contains("Stomme.Modules.DataExport", StringComparison.Ordinal));

                var folder = await ExportsFolderAsync(enabled);
                Assert.NotNull(folder);
                Assert.Equal(FolderTypes.Private, folder!.Type);

                // ---- and ONCE: a second pass adds no second page and no second folder, and touches neither ----------
                await ReconcileAsync(enabled);
                await ReconcileExportFolderAsync(enabled);
                Assert.Single(await AllPagePathsAsync(enabled), p => p == DataExportAdminPagePath);
                Assert.Equal(folder.FolderId, (await ExportsFolderAsync(enabled))!.FolderId);
                Assert.Equal(1, await InTenantAsync(enabled, async sp =>
                {
                    var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                    return (await sp.GetRequiredService<IFolderRepository>().GetFoldersAsync(alias.SiteId))
                        .Count(f => f.Path == Stomme.Modules.DataExport.Execution.ExportsFolder.FolderPath);
                }));
            }
            finally
            {
                await SafeShutdownAsync(enabled);
            }
        }

        private static Task<Folder?> ExportsFolderAsync(WebApplication app) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                return (Folder?)await sp.GetRequiredService<Stomme.Modules.DataExport.Execution.ExportsFolder>().FindAsync(alias.SiteId);
            });

        // The module's ISiteContentReconciler, run the way the framework's runner runs it: per site, inside the site's scope.
        private static Task ReconcileExportFolderAsync(WebApplication app) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                var site = await sp.GetRequiredService<ISiteRepository>().GetSiteAsync(alias.SiteId);
                var reconciler = sp.GetServices<Stomme.Infrastructure.ISiteContentReconciler>()
                    .Single(r => r is Stomme.Modules.DataExport.Execution.DataExportSiteReconciler);
                await reconciler.ReconcileAsync(new Stomme.Infrastructure.SiteContentReconciliationContext(
                    sp, alias, site, Stomme.Infrastructure.Tenancy.FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!));
                return true;
            });

        private const string LicensingAdminPagePath = "admin/licensing";

        [Fact]
        public async Task The_licensing_admin_page_is_added_when_an_installed_site_is_switched_to_OnPrem()
        {
            using var scratch = new ScratchInstall();

            // ---- Phase 1: installed with the mode unset ----------------------------------------------------
            scratch.SetDeploymentMode(null);
            var hosted = await BuildAsync(scratch);
            try
            {
                await InstallAsync(hosted);
                Assert.Null(await FindPageAsync(hosted, LicensingAdminPagePath));
                _output.WriteLine($"phase 1: installed with Deployment:Mode unset - no '{LicensingAdminPagePath}'");
            }
            finally
            {
                await SafeShutdownAsync(hosted);
            }

            // ---- Phase 2: same database, Deployment:Mode=OnPrem ----------------------------------------------
            scratch.SetDeploymentMode("OnPrem");
            var onPrem = await BuildAsync(scratch);
            try
            {
                Assert.NotNull(await FindAdminModuleDefinitionAsync(onPrem, "Licensing"));
                Assert.Null(await FindPageAsync(onPrem, LicensingAdminPagePath));

                await ReconcileAsync(onPrem);

                var page = await FindPageAsync(onPrem, LicensingAdminPagePath);
                Assert.NotNull(page);
                _output.WriteLine($"phase 2: reconciled - '{page!.Path}' now exists (PageId {page.PageId})");

                Assert.Contains(page.PermissionList,
                    p => p.PermissionName == PermissionNames.View && p.RoleName == RoleNames.Host && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Admin && p.IsAuthorized);
                Assert.DoesNotContain(page.PermissionList, p => p.RoleName == RoleNames.Everyone && p.IsAuthorized);

                var modules = await PageModuleNamesAsync(onPrem, page.PageId);
                _output.WriteLine($"modules on the page: {string.Join(", ", modules)}");
                Assert.Contains(modules, name => name != null && name.Contains("Admin.Licensing", StringComparison.Ordinal));

                await ReconcileAsync(onPrem);
                Assert.Single(await AllPagePathsAsync(onPrem), p => p == LicensingAdminPagePath);
            }
            finally
            {
                await SafeShutdownAsync(onPrem);
            }
        }

        [Fact]
        public async Task The_licensing_admin_page_is_never_added_while_the_mode_stays_unset()
        {
            using var scratch = new ScratchInstall();

            scratch.SetDeploymentMode(null);
            scratch.SetOpenIddictEnabled(false);
            var first = await BuildAsync(scratch);
            try
            {
                await InstallAsync(first);
                Assert.Null(await FindPageAsync(first, LicensingAdminPagePath));
            }
            finally
            {
                await SafeShutdownAsync(first);
            }

            // The second boot still has the mode unset. OpenIddict is switched on so the reconciliation provably HAS
            // work to do - an absent licensing page cannot then be a reconciliation that did nothing at all.
            scratch.SetOpenIddictEnabled(true);
            var second = await BuildAsync(scratch);
            try
            {
                await ReconcileAsync(second);

                Assert.NotNull(await FindPageAsync(second, NewAdminPagePath));
                Assert.Null(await FindPageAsync(second, LicensingAdminPagePath));
                _output.WriteLine($"mode unset on both boots: '{NewAdminPagePath}' added, '{LicensingAdminPagePath}' never");
            }
            finally
            {
                await SafeShutdownAsync(second);
            }
        }
        private static Task<WebApplication> BuildAsync(ScratchInstall scratch) =>
            Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

        // ---- helpers --------------------------------------------------------------------------------------

        /// <summary>Runs the reconciliation exactly as the boot/upgrade path in DatabaseManager does.</summary>
        private static async Task ReconcileAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);

            var sites = sp.GetRequiredService<ISiteRepository>();
            var site = await sites.GetSiteAsync(alias.SiteId);
            await sites.ProcessAdminPageTemplatesAsync(alias, site);
        }

        private static async Task<T> InTenantAsync<T>(WebApplication app, Func<IServiceProvider, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return await work(sp);
        }

        private static Task<Page?> FindPageAsync(WebApplication app, string path) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId);
                return pages.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
            });

        private static Task<List<string>> AllPagePathsAsync(WebApplication app) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                var pages = await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId);
                return pages.Select(p => p.Path).ToList();
            });

        private static Task UpdatePageAsync(WebApplication app, Page page) =>
            InTenantAsync(app, async sp =>
            {
                await sp.GetRequiredService<IPageRepository>().UpdatePageAsync(page);
                return true;
            });

        private static Task<List<string?>> PageModuleNamesAsync(WebApplication app, int pageId) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                var pageModules = await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId);
                return pageModules
                    .Where(pm => pm.PageId == pageId)
                    .Select(pm => pm.Module?.ModuleDefinitionName)
                    .ToList();
            });

        /// <summary>A module definition whose name starts with <paramref name="prefix"/> - for a module that is not one of the built-in Admin controls.</summary>
        private static Task<ModuleDefinition?> FindModuleDefinitionAsync(WebApplication app, string prefix) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>()
                    .GetModuleDefinitionsAsync(alias.SiteId);
                return definitions.FirstOrDefault(d =>
                    d.ModuleDefinitionName != null && d.ModuleDefinitionName.StartsWith(prefix, StringComparison.Ordinal));
            });

        private static Task<ModuleDefinition?> FindAdminModuleDefinitionAsync(WebApplication app, string shortName) =>
            InTenantAsync(app, async sp =>
            {
                var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost);
                var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>()
                    .GetModuleDefinitionsAsync(alias.SiteId);
                return definitions.FirstOrDefault(d =>
                    d.ModuleDefinitionName != null &&
                    d.ModuleDefinitionName.StartsWith($"Stomme.Modules.Admin.{shortName},", StringComparison.Ordinal));
            });

        private async Task InstallAsync(WebApplication app)
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

        /// <summary>
        /// A throwaway SQLite install whose <c>OpenIddict:Enabled</c> flag can be flipped BETWEEN app builds,
        /// so two hosts run against one database — the shape of an upgrade.
        /// </summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string? _previousOverridePath;
            private readonly string _overrideFile;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-adminupgrade-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                _overrideFile = Path.Combine(ContentRoot, "appsettings.override.json");

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
                        ["HostUsername"] = HostUsername,
                        ["HostPassword"] = HostPassword,
                        ["HostEmail"] = HostEmail,
                        ["HostName"] = "Host"
                    }
                };
                Write(overrides);

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            /// <summary>
            /// Flips the authority flag in place. Re-reads the file first, because the install WRITES to it
            /// (DefaultDBType, the connection string, InstallationId — D32/D-4); rewriting from scratch would
            /// discard that and make phase 2 a fresh install rather than an upgrade.
            /// </summary>
            /// <summary>Sets (or, for <c>null</c>, removes) <c>DataExport:Enabled</c> in place.</summary>
            public void SetDataExportEnabled(bool? enabled)
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(_overrideFile))?.AsObject() ?? new JsonObject();
                if (enabled == null)
                {
                    root.Remove("DataExport");
                }
                else
                {
                    root["DataExport"] = new JsonObject { ["Enabled"] = enabled.Value };
                }

                Write(root);
            }

            public void SetOpenIddictEnabled(bool enabled)
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(_overrideFile))?.AsObject() ?? new JsonObject();
                root["OpenIddict"] = new JsonObject { ["Enabled"] = enabled };
                Write(root);
            }

            /// <summary>
            /// Sets (or removes) the MCP switches in place - the same shape as
            /// <see cref="SetOpenIddictEnabled"/>. Removing them is what an OLDER release looks like: the section is
            /// absent, not present-and-false, because a release that predates the feature wrote no key at all.
            /// </summary>
            public void SetMcpEnabled(bool? enabled)
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(_overrideFile))?.AsObject() ?? new JsonObject();
                if (enabled is null)
                {
                    root.Remove("Mcp");
                }
                else
                {
                    root["Mcp"] = new JsonObject
                    {
                        ["Enabled"] = enabled.Value,
                        ["Server"] = new JsonObject { ["Enabled"] = enabled.Value },
                    };
                }

                Write(root);
            }

            /// <summary>Sets (or, for <c>null</c>, removes) <c>Help:Enabled</c> in place - absent is what an older release wrote.</summary>
            public void SetHelpEnabled(bool? enabled)
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(_overrideFile))?.AsObject() ?? new JsonObject();
                if (enabled is null)
                {
                    root.Remove("Help");
                }
                else
                {
                    root["Help"] = new JsonObject { ["Enabled"] = enabled.Value };
                }

                Write(root);
            }

            /// <summary>
            /// Sets (or, for <c>null</c>, removes) <c>Deployment:Mode</c> in place, re-reading the file first for the same
            /// reason as <see cref="SetOpenIddictEnabled"/>.
            /// </summary>
            public void SetDeploymentMode(string? mode)
            {
                var root = JsonNode.Parse(System.IO.File.ReadAllText(_overrideFile))?.AsObject() ?? new JsonObject();
                if (mode == null)
                {
                    root.Remove("Deployment");
                }
                else
                {
                    root["Deployment"] = new JsonObject { ["Mode"] = mode };
                }

                Write(root);
            }
            private void Write(JsonObject root) =>
                System.IO.File.WriteAllText(_overrideFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);
                ScratchSqlite.ReleaseInstall(ContentRoot);   // this install's pools only
                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}
