using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The metering module on an INSTALLED site, on all three stores: its pages exist and are served, usage
    /// recorded through the real port reaches the ledger, a quota defined over HTTP makes enforcement flip, and the
    /// host logs nothing it is unhappy about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>HTTP 200 is a boot check, not a render check.</b> A misconfigured host serves a perfectly healthy 200 of
    /// the INSTALLER, which is how a dead master shipped twice here - so the page assertions below are
    /// discriminating: the installer markers must be ABSENT and the module's own page title present.
    /// </para>
    /// <para>
    /// <b>MEASURED BOUNDARY - this suite does NOT assert the module's own markup, and that is a property of the
    /// transport, not a gap in the module.</b> A clean-arch Client module control emits NOTHING through the
    /// TestServer transport on this path: on <c>/usage</c> and <c>/admin/usage</c> neither the control's markup nor
    /// its "you do not have access" branch appears, only the container's module anchor - and the SIBLING
    /// Notifications module behaves identically on its own page. That is not vacuous reasoning: the same request
    /// pipeline DOES render module content, which <see cref="The_transport_can_render_module_content_at_all"/>
    /// proves with HtmlText's seeded text. So the control layer of these modules is reachable only by a REAL
    /// BROWSER, and the BL-54 class of defect (a module <c>_Imports.razor</c> missing
    /// <c>@using Microsoft.AspNetCore.Components.Web</c>, which compiles green, renders, and leaves every
    /// <c>@onclick</c> silently dead) stays OPEN for this module - recorded in its README, not papered over.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class InstalledSiteMeteringDriveTests
    {
        private const string Host = "localhost";
        private const string Metric = "tests.driven";

        private readonly ITestOutputHelper _output;

        public InstalledSiteMeteringDriveTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task An_installed_site_serves_the_usage_pages_and_enforcement_flips_when_a_quota_is_defined(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            // Through appsettings.override.json - where a deployed install keeps it - and NOT builder.Configuration: the host's
            // IConfiguration does not include the override, and the jobs read the merged root (HostConfiguration, BL-162).
            using var scratch = new ScratchGateInstall(store, Host, colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new System.Text.Json.Nodes.JsonObject { ["Metering"] = new System.Text.Json.Nodes.JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                // ---- the module's pages exist on the installed site, and carry ITS module ---------------------
                var placed = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var pages = (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).ToList();
                    var admin = pages.SingleOrDefault(page => page.Path == "admin/usage");
                    Assert.NotNull(admin);
                    Assert.Contains(pages, page => page.Path == "usage");

                    return (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                        .Where(module => module.PageId == admin.PageId)
                        .Select(module => module.Title)
                        .ToList();
                });

                _output.WriteLine($"[{storeName}] admin/usage carries: {string.Join(", ", placed)}");
                Assert.Contains("Metering", placed);

                // ---- record through the REAL port, with no quota defined --------------------------------------
                var before = await RecordAsync(app, 2);
                _output.WriteLine($"[{storeName}] undefined metric: recorded={before.Recorded} within={before.WithinQuota} reason={before.Reason}");
                Assert.True(before.Recorded);
                Assert.True(before.WithinQuota);
                Assert.Equal(QuotaDecisionReason.Undefined, before.Reason);

                // ---- the pages are SERVED, and they are the module's, not the installer's ---------------------
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                foreach (var path in new[] { "/usage", "/admin/usage" })
                {
                    var served = await GateDrive.GetAsync(client, cookies, Host, path, _output, path);
                    _output.WriteLine($"[{storeName}] GET {path} -> {(int)served.Status}, {served.Body.Length} chars");

                    Assert.Equal(HttpStatusCode.OK, served.Status);
                    Assert.DoesNotContain("Installation Wizard", served.Body);
                    Assert.DoesNotContain("Default Site Template", served.Body);
                    Assert.Contains("<title>Usage", served.Body);
                }

                // ---- define a quota over HTTP and watch enforcement FLIP --------------------------------------
                var define = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostUserId, HttpMethod.Post, "/api/Metering/quotas",
                    new { Metric, Period = "Monthly", Limit = 1, WarnAtPercent = 0 }, _output, "define");
                _output.WriteLine($"[{storeName}] POST quota -> {(int)define.Status}");
                Assert.Equal(HttpStatusCode.OK, define.Status);

                // A quota defined AFTER consumption starts at zero: the 2 units above are in the ledger but not in
                // the counter, because Define snaps a fresh window and the repair pass has not run. That is the
                // honest behaviour - defining a limit today does not retroactively charge yesterday - and it makes
                // the arithmetic predictable: limit 1, so the first unit after definition is within, the next is not.
                var within = await RecordAsync(app, 1);
                var over = await RecordAsync(app, 1);
                _output.WriteLine($"[{storeName}] within={within.WithinQuota}/{within.Reason}; over={over.WithinQuota}/{over.Reason}");

                Assert.True(within.Recorded && over.Recorded);
                Assert.True(within.WithinQuota);
                Assert.False(over.WithinQuota);
                Assert.Equal(QuotaDecisionReason.Exceeded, over.Reason);

                // The inspector agrees BEFORE consumption - the answer a producer actually asks for.
                var decision = await GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                    sp.GetRequiredService<IQuotaInspector>().EvaluateAsync(Metric, 1));
                _output.WriteLine($"[{storeName}] inspector: allowed={decision.Allowed} reason={decision.Reason} consumed={decision.Consumed} limit={decision.Limit}");
                Assert.False(decision.Allowed);

                // ---- the quota the operator just defined is readable back over HTTP ---------------------------
                var quotas = await GateDrive.GetAsync(client, cookies, Host, "/api/Metering/quotas", _output, "quotas");
                Assert.Equal(HttpStatusCode.OK, quotas.Status);
                Assert.Contains(Metric, quotas.Body);
                _output.WriteLine($"[{storeName}] quotas: {GateDrive.Truncate(quotas.Body)}");

                // ---- nothing left half-done, and the host is not complaining ----------------------------------
                var pending = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IUsageRecordRepository>().ListBySiteAsync(siteId)).Count(row => row.FoldState == FoldState.Pending));
                Assert.Equal(0, pending);

                var errors = GateDrive.DrainCapturedErrors();
                _output.WriteLine($"[{storeName}] host errors: {(errors.Count == 0 ? "(none)" : string.Join(" || ", errors))}");
                Assert.Empty(errors);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// The vacuity guard for the boundary stated in this class's remarks: the transport CAN render module
        /// content, so "the metering control's markup is absent" is a fact about which modules this path renders -
        /// not about a request pipeline that renders nothing at all.
        /// </summary>
        /// <remarks>
        /// HtmlText's seeded "Welcome To Stomme" can only appear if a module was discovered, its content read and
        /// its component rendered - the same assertion the D30 render guard leans on.
        /// </remarks>
        [Fact]
        public async Task The_transport_can_render_module_content_at_all()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var home = await GateDrive.GetAsync(client, cookies: null, Host, "/", _output, "home");

                _output.WriteLine($"[guard] GET / -> {(int)home.Status}, {home.Body.Length} chars; HtmlText content present = {home.Body.Contains("Welcome To Stomme", StringComparison.Ordinal)}");

                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.Contains("Welcome To Stomme", home.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<UsageOutcome> RecordAsync(WebApplication app, long quantity) =>
            GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                sp.GetRequiredService<IUsageRecorder>().RecordAsync(new UsageEvent(Metric, quantity, Guid.NewGuid(), "drive")));
    }
}
