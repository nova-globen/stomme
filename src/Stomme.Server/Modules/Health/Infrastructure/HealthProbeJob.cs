#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Health.Application;
using Stomme.Repository;
using Stomme.Tenancy.Abstractions;
using Stomme.Transport.Realtime;
using Stomme.Persistence.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Health.Infrastructure
{
    /// <summary>
    /// Runs the probes every minute, records what they said, and decides whether to tell anyone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The job calls the pass directly rather than sending a gated message.</b> The outermost
    /// <c>AuthorizationBehavior</c> is unconditional and denies when there is no <c>HttpContext</c>; a job has
    /// none, so every tick would throw <c>AccessDeniedException</c> into the job's catch and do nothing — while
    /// reporting success. Gating belongs on the OPERATOR messages (run-now, acknowledge), which do have a caller.
    /// </para>
    /// <para>
    /// <b>The tick has a budget.</b> Without one, a dependency that stops answering stops health monitoring for
    /// as long as it takes to give up. The budget bounds the whole tick, the runner bounds each probe inside it,
    /// and each tenant and each site is caught separately, so nothing that goes wrong in one place costs another.
    /// </para>
    /// <para>
    /// <b>Scopes.</b> The job enters the tenant's ambient scope itself before creating any DI scope, so it is
    /// correct whatever scope its caller hands it. <c>HostedServiceBase</c> does this too
    /// (<c>ff19714</c>); this does not depend on that.
    /// </para>
    /// </remarks>
    public class HealthProbeJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.Health.Infrastructure.HealthProbeJob, Stomme.Server"

        /// <summary>The whole tick's bound, per tenant.</summary>
        internal static readonly TimeSpan TickBudget = TimeSpan.FromSeconds(45);

        /// <summary>What the tick's log says of a scope it left to the run-now already probing it.</summary>
        internal const string SkippedInFlight = "Skipped - A Run Now Is In Flight";

        public HealthProbeJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Health Probe Job";
            Frequency = "m"; // minute
            Interval = 1;
            IsEnabled = true; // gated at run time by Health:Enabled
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            if (!HealthSettings.ProbingEnabled(HostConfiguration.Resolve(provider)))
            {
                return "Health Probing Is Disabled (Health:Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Health Probe: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<HealthProbeJob>>();
            var snapshot = provider.GetRequiredService<HealthSnapshot>();

            using var tenantScope = provider.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);
            using var budget = new CancellationTokenSource(TickBudget);

            List<Site> sites;
            using (var listing = scopes.CreateScope())
            {
                listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync())
                    .Where(site => !site.IsDeleted).ToList();
            }

            var log = new StringBuilder();
            var worst = HealthState.Healthy;

            // The live update: absent unless the SignalR transport is on. Resolved from the job's provider - a
            // singleton port - and never allowed to cost the tick.
            var push = provider.GetService<IHealthRealtimePush>();

            // ONE pass in flight per (tenant, site), shared with an operator's run-now (found by the exemplar
            // module's drive): a pass reads a subject's alert state and INSERTS it when there is none, so two passes over one
            // site at once - the tick and a "Run now" - both inserted a new subject's row and the second failed on its key,
            // answering the operator 500. The tick skips what a run-now holds; a run-now is refused (409) while the tick holds it.
            var gate = provider.GetRequiredService<HealthRunNowGate>();

            // The mail-server probe connects to the relay, so it runs on its own interval (Health:MailServerProbeIntervalMinutes).
            var mailInterval = HealthSettings.MailServerProbeIntervalMinutes(HostConfiguration.Resolve(provider));
            var mailSchedule = provider.GetRequiredService<HealthMailServerSchedule>();
            var now = DateTime.UtcNow;

            // The wider scopes run once per tenant, against site 0 - not once per site. That is what makes a
            // shared store going down one alert rather than one for every site standing on it.
            if (gate.TryEnter(tenantId.Value, 0))
            {
                try
                {
                    foreach (var scope in new[] { HealthProbeScope.Installation, HealthProbeScope.Tenant })
                    {
                        var result = await RunScopeAsync(scopes, tenant!, tenantId, scope, siteId: 0, logger, budget.Token).ConfigureAwait(false);
                        if (result is not null)
                        {
                            Append(log, scope.ToString(), result);
                            worst = result.Worst > worst ? result.Worst : worst;
                        }
                    }
                }
                finally
                {
                    gate.Exit(tenantId.Value, 0);
                }
            }
            else
            {
                log.Append("Health Installation and Tenant: ").Append(SkippedInFlight).Append("<br />");
            }

            // A site's page shows the shared scopes beside its own, so what it is told is the worst of both.
            var shared = worst;

            foreach (var site in sites)
            {
                if (!gate.TryEnter(tenantId.Value, site.SiteId))
                {
                    log.Append("Health Site ").Append(site.Name).Append(": ").Append(SkippedInFlight).Append("<br />");
                    continue;
                }

                HealthPassResult? result;
                try
                {
                    var includeMailServer = mailSchedule.TakeIfDue(tenantId.Value, site.SiteId, now, mailInterval);
                    result = await RunScopeAsync(scopes, tenant!, tenantId, HealthProbeScope.Site, site.SiteId, logger, budget.Token, site.Name, includeMailServer).ConfigureAwait(false);
                }
                finally
                {
                    gate.Exit(tenantId.Value, site.SiteId);
                }

                if (result is not null)
                {
                    Append(log, "Site " + site.Name, result);
                    worst = result.Worst > worst ? result.Worst : worst;

                    if (push is not null)
                    {
                        await PushAsync(push, tenant!.TenantId, site.SiteId, result.Worst > shared ? result.Worst : shared, logger).ConfigureAwait(false);
                    }
                }
            }

            // The readiness endpoint reads THIS, never a live probe: an anonymous caller must not be able to
            // drive database round-trips, cache writes and SMTP connects by refreshing a page.
            snapshot.Record(worst, DateTime.UtcNow);

            return log.Length == 0 ? "Health Probe: Nothing To Probe<br />" : log.ToString();
        }

        /// <summary>
        /// Pushes one site's state word to that site's open health pages. Contained: a transport that fails must not cost
        /// the tick, and the page still reads on load and on Refresh.
        /// </summary>
        internal static async Task PushAsync(IHealthRealtimePush push, int tenantId, int siteId, HealthState state, ILogger logger)
        {
            try
            {
                await push.PushAsync(tenantId, siteId, new HealthRealtimeMessage(state.ToString(), DateTime.UtcNow)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The health state could not be pushed to site {SiteId} of tenant {TenantId}.", siteId, tenantId);
            }
        }

        private static void Append(StringBuilder log, string what, HealthPassResult result) =>
            log.Append("Health ").Append(what).Append(": ")
                .Append(result.ProbesRun).Append(" Probe(s), ")
                .Append(result.RowsWritten).Append(" Row(s), ")
                .Append(result.AlertsRaised).Append(" Raised, ")
                .Append(result.RecoveriesSent).Append(" Recovered - ")
                .Append(result.Worst).Append("<br />");

        private static async Task<HealthPassResult?> RunScopeAsync(
            IServiceScopeFactory scopes,
            Tenant tenant,
            Tenancy.Domain.TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            ILogger logger,
            CancellationToken cancellationToken,
            string? siteName = null,
            bool includeMailServer = true)
        {
            using var siteScope = scopes.CreateScope();
            try
            {
                var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                tenantManager.SetTenant(tenant);

                if (siteId > 0)
                {
                    await tenantManager.SetAliasAsync(tenant.TenantId, siteId);
                    if (siteScope.ServiceProvider.GetRequiredService<Shared.SiteState>().Alias is null)
                    {
                        tenantManager.SetTenant(tenant);
                    }
                }

                var pass = siteScope.ServiceProvider.GetRequiredService<HealthProbePass>();
                return await pass.RunAsync(tenantId, scope, siteId, cancellationToken, includeMailServer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Contained per scope and per site: one failure must not cost the rest of the tick.
                logger.LogError(ex, "The health probe pass failed for {Scope} {SiteName} of tenant {TenantId}.",
                    scope, siteName ?? siteId.ToString(), tenant.TenantId);
                return null;
            }
        }
    }
}
