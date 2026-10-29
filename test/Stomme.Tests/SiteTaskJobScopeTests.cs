#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>SiteTaskJob</c> runs each site's task with THAT site: the provider a task gets
    /// resolves the task's own site and tenant, for two sites of one tenant drained in ONE job run, in the order the
    /// loop takes them. A task that read the first site's state on the second site would act on the wrong site.
    /// </summary>
    /// <remarks>
    /// Measured: green as the code stands; with the per-task <c>SetAliasAsync</c> removed both tasks resolve
    /// site -1 and it fails. The cross-TENANT half is <c>HostedServiceBase</c>'s per-tenant scope
    /// (<see cref="HostedServiceBaseTenantIsolationTests"/>), so no job run ever carries two tenants.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class SiteTaskJobScopeTests(ITestOutputHelper output)
    {
        private const string HostA = "localhost";
        private const string HostB = "sitetask-b.localhost";

        /// <summary>Reports what the provider it was handed says: the site and tenant it resolves.</summary>
        public sealed class ProbeTask : ISiteTask
        {
            public string ExecuteTask(IServiceProvider provider, Site site, string parameters) => string.Empty;

            public Task<string> ExecuteTaskAsync(IServiceProvider provider, Site site, string parameters)
            {
                var alias = provider.GetRequiredService<SiteState>().Alias;
                var tenant = provider.GetRequiredService<ITenantContext>().CurrentTenantId;
                return Task.FromResult($"asked={site.SiteId};resolved={alias?.SiteId};tenant={tenant?.Value}");
            }
        }

        [Fact]
        public async Task Each_sites_task_resolves_its_own_site_in_one_job_run()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), HostA, output);
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var aliasA = await GateDrive.AliasAsync(app, HostA);
                var siteB = await GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
                {
                    var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                    {
                        Name = "Site Task B", TenantId = alias.TenantId,
                        DefaultThemeType = Constants.DefaultTheme, DefaultContainerType = Constants.DefaultContainer,
                        AdminContainerType = Constants.DefaultAdminContainer, SiteTemplateType = Constants.DefaultSiteTemplate,
                        RenderMode = RenderModes.Static, Runtime = Runtimes.Server, Prerender = false, Hybrid = false,
                        EnhancedNavigation = true, CultureCode = "en", AllowRegistration = false, CaptureBrokenUrls = false,
                        VisitorTracking = false, PwaIsEnabled = false,
                    });
                    await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias { Name = HostB, TenantId = alias.TenantId, SiteId = site.SiteId, IsDefault = true });
                    return site.SiteId;
                });

                var probe = typeof(ProbeTask).AssemblyQualifiedName!;
                var (taskA, taskB) = await GateDrive.InTenantAsync(app, HostA, async (sp, _) =>
                {
                    var tasks = sp.GetRequiredService<ISiteTaskRepository>();
                    var a = await tasks.AddSiteTaskAsync(new SiteTask { SiteId = aliasA.SiteId, Name = "probe A", Type = probe, Parameters = string.Empty, IsCompleted = false, Status = string.Empty });
                    var b = await tasks.AddSiteTaskAsync(new SiteTask { SiteId = siteB, Name = "probe B", Type = probe, Parameters = string.Empty, IsCompleted = false, Status = string.Empty });
                    return (a.SiteTaskId, b.SiteTaskId);
                });

                // ExecuteJobAsync exactly as HostedServiceBase runs it for this tenant: its tenant scope, one job scope.
                var tenant = await GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
                    (await sp.GetRequiredService<ITenantRepository>().GetTenantsAsync()).Single(t => t.TenantId == alias.TenantId));
                string notes;
                using (app.Services.GetRequiredService<Stomme.Persistence.Abstractions.ITenantScopeFactory>().BeginScope(Stomme.Infrastructure.Tenancy.FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId)!.Value))
                using (var jobScope = app.Services.CreateScope())
                {
                    jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                    notes = await new SiteTaskJob(app.Services.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(jobScope.ServiceProvider);
                }

                output.WriteLine("[job] " + notes);
                var (statusA, statusB) = await GateDrive.InTenantAsync(app, HostA, async (sp, _) =>
                {
                    var tasks = sp.GetRequiredService<ISiteTaskRepository>();
                    return ((await tasks.GetSiteTaskAsync(taskA)).Status, (await tasks.GetSiteTaskAsync(taskB)).Status);
                });
                output.WriteLine($"[A] {statusA}");
                output.WriteLine($"[B] {statusB}");

                var tenantGuid = Stomme.Infrastructure.Tenancy.FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId)!.Value;
                Assert.Equal($"asked={aliasA.SiteId};resolved={aliasA.SiteId};tenant={tenantGuid}", statusA);
                Assert.Equal($"asked={siteB};resolved={siteB};tenant={tenantGuid}", statusB);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
