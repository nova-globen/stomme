#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure
{
    /// <summary>What a <see cref="ISiteContentReconciler"/> is handed for one site: the site, its alias, and the scope it runs in.</summary>
    /// <param name="Services">A FRESH DI scope created inside the site's tenant scope, with the site's alias set on it.</param>
    /// <param name="Alias">The site's default alias (the one the scope was routed with).</param>
    /// <param name="Site">The site being reconciled.</param>
    /// <param name="TenantId">The persistence seam's id for the site's tenant (the ambient tenant scope).</param>
    public sealed record SiteContentReconciliationContext(IServiceProvider Services, Alias Alias, Site Site, TenantId TenantId);

    /// <summary>
    /// Module-owned content a site must carry, brought up to date on the boot/upgrade path - ADDITIVELY: create what a
    /// newer release declares and the site lacks, never re-process what exists (an operator's edit must survive every
    /// boot). The notification templates are the first implementation; metering and help topics use
    /// the same seam.
    /// </summary>
    /// <remarks>
    /// Registered in DI (scoped) by a module startup. The framework's <see cref="SiteContentReconciliationRunner"/> is
    /// the only caller; a host with no registration does nothing.
    /// </remarks>
    public interface ISiteContentReconciler
    {
        /// <summary>A human name, used in the log line that reports a failure.</summary>
        string Name { get; }

        Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default);
    }

    /// <summary>The outcome of one reconciliation run: the sites visited and every contained failure.</summary>
    public sealed record SiteContentReconciliationReport(int SitesVisited, IReadOnlyList<string> Failures)
    {
        public static readonly SiteContentReconciliationReport Empty = new(0, []);
    }

    /// <summary>
    /// Runs every <see cref="ISiteContentReconciler"/> for every site of every tenant, on the boot/upgrade path, after
    /// the framework's own site steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not inside <c>DatabaseManager.InitializeSitesAsync</c>'s loop.</b>
    /// That loop walks every tenant and site inside ONE DI scope, where <c>StommeTenantContext</c> caches the FIRST
    /// alias's tenant, and wraps the whole loop in one catch. A module reconciler called there would write every
    /// tenant's content under tenant 1, and one throw would abort the framework page reconciliation for every site
    /// after it. Here: the tenant scope is entered BEFORE a fresh DI scope is created (the <c>FeatureSeeder</c> order,
    /// pinned by <c>HostCompositionTests.The_tenant_scope_flows_into_a_di_scope_created_inside_it</c>), each site gets
    /// its own DI scope, and each reconciler's failure is logged by name and contained to that site.
    /// The framework loop had the same defect for its OWN writes - a second tenant's
    /// pages landed in the first tenant's database - and now takes this runner's shape (a tenant scope, a DI scope per
    /// site, a failure contained to its site). This runner still runs after it, whatever happened to a site there.
    /// </para>
    /// <para>
    /// <b>That is TWO independent guards, and each is sufficient on its own - MEASURED.</b>
    /// <c>StommeTenantContext</c> answers from an ambient <c>BeginScope</c> first, else from <c>SiteState.Alias</c> once
    /// per DI scope. So removing only the <c>BeginScope</c> is harmless here (each site's fresh scope has its alias set
    /// before anything reads the tenant), and so is sharing one DI scope across sites while keeping it (the ambient
    /// tenant wins). Both are EQUIVALENT mutants against <c>HelpTopicReconciliationTests</c>; removing BOTH - which was
    /// <c>InitializeSitesAsync</c>'s earlier shape - turns it red on SQLite, PostgreSQL and RavenDB. Keep both: each covers a
    /// caller the other does not (a service that reads the tenant during scope construction; a scope reused by mistake).
    /// </para>
    /// <para>
    /// <b>The module stores are brought up FIRST (measured).</b> This path runs from <c>Program.Main</c>'s
    /// <c>InstallAsync</c>, BEFORE <c>app.RunAsync()</c> starts any hosted service - and the relational module store's
    /// schema (<c>EnsureCreated</c> + the missing-table catch-up) is created by
    /// <c>ModuleAggregateStoreInitializer</c>, a hosted service. So on a fresh install, and on the first boot of a
    /// release that enrols a new aggregate, the tables a reconciler writes do not exist yet. The runner therefore runs
    /// the SAME schema step first (idempotent; the initializer repeats it harmlessly when the host starts).
    /// </para>
    /// </remarks>
    public sealed class SiteContentReconciliationRunner
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger _logger;

        public SiteContentReconciliationRunner(IServiceScopeFactory scopes, ILogger logger)
        {
            _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SiteContentReconciliationReport> RunAsync(CancellationToken ct = default)
        {
            List<Tenant> tenants;
            List<Alias> aliases;
            ITenantScopeFactory tenantScopes;

            using (var probe = _scopes.CreateScope())
            {
                if (!probe.ServiceProvider.GetServices<ISiteContentReconciler>().Any())
                {
                    return SiteContentReconciliationReport.Empty;
                }

                await BringUpModuleStoresAsync(probe.ServiceProvider, ct);

                tenantScopes = probe.ServiceProvider.GetRequiredService<ITenantScopeFactory>();
                tenants = (await probe.ServiceProvider.GetRequiredService<ITenantRepository>().GetTenantsAsync()).ToList();
                aliases = (await probe.ServiceProvider.GetRequiredService<IAliasRepository>().GetAliasesAsync()).ToList();
            }

            var failures = new List<string>();
            var visited = 0;
            foreach (var tenant in tenants)
            {
                var tenantId = FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
                if (tenantId is null)
                {
                    continue;
                }

                using var tenantScope = tenantScopes.BeginScope(tenantId.Value);

                List<Site> sites;
                try
                {
                    using var listing = _scopes.CreateScope();
                    listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                    sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync())
                        .Where(site => !site.IsDeleted)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Site content reconciliation could not list the sites of tenant {TenantName} ({TenantId}).", tenant.Name, tenant.TenantId);
                    failures.Add($"tenant {tenant.TenantId}: {ex.GetType().Name}");
                    continue;
                }

                foreach (var site in sites)
                {
                    var alias = aliases.FirstOrDefault(item => item.TenantId == tenant.TenantId && item.SiteId == site.SiteId && item.IsDefault)
                        ?? aliases.FirstOrDefault(item => item.TenantId == tenant.TenantId && item.SiteId == site.SiteId);
                    if (alias is null)
                    {
                        continue; // a site with no alias is not served; the framework loop skips it too
                    }

                    visited++;
                    using var siteScope = _scopes.CreateScope();
                    try
                    {
                        await siteScope.ServiceProvider.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Site content reconciliation could not route to site {SiteName} ({SiteId}).", site.Name, site.SiteId);
                        failures.Add($"site {site.SiteId}: {ex.GetType().Name}");
                        continue;
                    }

                    var context = new SiteContentReconciliationContext(siteScope.ServiceProvider, alias, site, tenantId);
                    foreach (var reconciler in siteScope.ServiceProvider.GetServices<ISiteContentReconciler>())
                    {
                        try
                        {
                            await reconciler.ReconcileAsync(context, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                        {
                            // Contained per site and per reconciler: never swallowed silently, never the boot's end.
                            _logger.LogError(ex, "The site content reconciler '{Reconciler}' failed on site {SiteName} ({SiteId}) of tenant {TenantId}.",
                                reconciler.Name, site.Name, site.SiteId, tenant.TenantId);
                            failures.Add($"{reconciler.Name} on site {site.SiteId}: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }

            return new SiteContentReconciliationReport(visited, failures);
        }

        private Task BringUpModuleStoresAsync(IServiceProvider scoped, CancellationToken ct) =>
            Microsoft.Extensions.DependencyInjection.ModuleAggregateStoreInitializer.EnsureAllAsync(scoped, _logger, ct);
    }
}
