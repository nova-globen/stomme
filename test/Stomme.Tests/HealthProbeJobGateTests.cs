using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Infrastructure;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-86 increment 6 - the scheduled health tick and an operator's run-now share ONE pass per (tenant, site). Found by the
    /// exemplar module's drive: a pass reads a subject's alert state and inserts it when there is none, so a "Run now" during a
    /// tick inserted a new subject's row twice and the operator got a 500 (SQLite: UNIQUE constraint failed on the alert
    /// state's id). The run-now suite switches the job off precisely so this cannot happen, which is why no test met it.
    /// </summary>
    /// <remarks>
    /// Driven on an installed SQLite host whose scheduler is NOT started, so the only passes are the ones the test runs and
    /// the gate the test holds - the run-now side (409 while the gate is held) is <c>HealthRunNowDriveTests</c>'.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HealthProbeJobGateTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task The_tick_skips_a_site_and_the_shared_scopes_while_a_run_now_holds_them()
        {
            var settings = new JsonObject { ["Health"] = new JsonObject { ["Enabled"] = true } };
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, settings);
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var alias = await GateDrive.AliasAsync(app, Host);
                var tenant = FrameworkTenantId.ToTenantIdOrNull(alias.TenantId)!.Value;
                var gate = app.Services.GetRequiredService<HealthRunNowGate>();

                // Held: the tick probes neither the site nor the shared scopes, and writes nothing for them.
                Assert.True(gate.TryEnter(tenant, alias.SiteId));
                Assert.True(gate.TryEnter(tenant, 0));
                string held;
                try
                {
                    held = await RunJobAsync(app);
                }
                finally
                {
                    gate.Exit(tenant, alias.SiteId);
                    gate.Exit(tenant, 0);
                }

                output.WriteLine($"[held] {held}");
                Assert.Contains("Health Installation and Tenant: " + HealthProbeJob.SkippedInFlight, held);
                Assert.Contains(": " + HealthProbeJob.SkippedInFlight, held.Replace("Health Installation and Tenant: " + HealthProbeJob.SkippedInFlight, string.Empty));
                Assert.Equal((0, 0), (await AlertStatesAsync(app, 0), await AlertStatesAsync(app, alias.SiteId)));

                // Released: the next tick probes both and records their subjects.
                var released = await RunJobAsync(app);
                output.WriteLine($"[released] {released}");
                Assert.DoesNotContain(HealthProbeJob.SkippedInFlight, released);
                Assert.True(await AlertStatesAsync(app, 0) >= 1, "the shared scopes recorded no subject after the gate was released");
                // A stock site has no site-scope probe that applies (the mail probe needs a mail server), so its pass writes no
                // row - what proves it ran is the tick's own line for the site, where the held tick said "Skipped".
                Assert.Matches(@"Health Site [^:<]+: \d+ Probe\(s\)", released);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<int> AlertStatesAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) => (await sp.GetRequiredService<IHealthAlertStateRepository>().ListBySiteAsync(siteId)).Count);

        /// <summary>ExecuteJobAsync as <c>HostedServiceBase</c> invokes it: one job scope per tenant, the tenant set.</summary>
        private static async Task<string> RunJobAsync(WebApplication app)
        {
            using var scope = app.Services.CreateScope();
            var notes = string.Empty;
            foreach (var tenant in await scope.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync())
            {
                scope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                notes += await new HealthProbeJob(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(scope.ServiceProvider);
            }

            return notes;
        }
    }
}
