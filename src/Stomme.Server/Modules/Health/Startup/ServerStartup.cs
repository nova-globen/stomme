#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Mediator;
using Stomme.Models.Health;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Contributions;
using Stomme.Modules.Health.Domain;
using Stomme.Repository;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Health.Startup
{
    /// <summary>
    /// Wires the health module: two storage-blind aggregates, the contribution seam, and the probe pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovered by the framework's assembly scan like every other module startup. Both aggregates take one
    /// <c>AddModuleAggregatePersistence</c> call and the module authors no persistence code: on a relational
    /// install the host maps them into the shared context, on RavenDB or the File store they round-trip through
    /// the install's own store.
    /// </para>
    /// <para>
    /// <b>Contributors are registered as an ENUMERABLE.</b> That is the whole seam: the framework's own probes
    /// arrive through exactly the same registration a module uses, with no privileged path, so removing the
    /// framework contributor would leave a working module rather than a broken one.
    /// </para>
    /// <para>
    /// <b>Alerting is off unless it is switched on.</b> Without the <c>health.alerting</c> feature the no-op sink
    /// is registered, and the probe job still records everything it observes — so the history is already there
    /// when someone turns alerting on, rather than starting from the moment they did.
    /// </para>
    /// </remarks>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // no middleware
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // no MVC customization
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddModuleAggregatePersistence<HealthRollupAggregate, Guid>();
            services.AddModuleAggregatePersistence<HealthAlertStateAggregate, Guid>();

            services.AddScoped<IHealthRollupRepository, HealthRollupRepository>();
            services.AddScoped<IHealthAlertStateRepository, HealthAlertStateRepository>();

            // The thresholds, read once per scope from the Host-scope Setting rows with defensive fallbacks. It is
            // an ACCESSOR rather than a resolved HealthSettings because reading settings is asynchronous and a
            // factory is not: blocking here would be sync-over-async, which a build gate fails.
            services.TryAddScoped<HealthSettingsAccessor>();

            services.TryAddSingleton(sp => new HealthProbeRunner());

            // The readiness snapshot is a SINGLETON and deliberately not persisted: readiness answers "should
            // traffic come to THIS process", so a value read from a shared store would answer for another host.
            services.TryAddSingleton<HealthSnapshot>();

            // The framework's own probes and metrics, through the same seam a module uses.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IHealthContributor, StommeDependencyContributor>());
            // The file rows whose file is gone from disk, per site.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IHealthContributor, MissingFilesContributor>());

            // ALERTING IS OFF UNLESS IT IS SWITCHED ON, and the probe job records either way - so the history is
            // already there when someone turns alerting on, rather than starting from the moment they did. The
            // switch is read here, at composition, because which SINK is registered is the cleanest expression of
            // "nobody is told": there is then no code path from a transition to a dispatcher at all.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Modules.Notifications.Application.Templates.INotificationTemplateSource, HealthTemplateSource>());
            services.TryAddScoped<IHealthAlertSink>(sp =>
                bool.TryParse(HostConfiguration.Resolve(sp)?["Health:Alerting"], out var on) && on
                    ? ActivatorUtilities.CreateInstance<NotificationHealthAlertSink>(sp)
                    : new NoOpHealthAlertSink());

            // The per-scope pass, resolved inside each scope's own DI scope by HealthProbeJob. The job itself is a
            // HostedServiceBase found by the framework's assembly scan, never AddHostedService'd here.
            services.AddScoped<HealthProbePass>();
            services.AddScoped<HealthRollupPass>();

            // Retention rides the framework's PurgeJob through the seam this row built, rather than a second
            // Job row an operator could disable without knowing what they switched off. The Minute series is NOT
            // purged here - it is trimmed on write, so it stays bounded even when PurgeJob IS disabled.
            services.AddScoped<IRetentionPurger, Infrastructure.HealthRetentionPurger>();

            // The gated read surface. Handlers are registered EXPLICITLY - never AddMediator() here:
            // an earlier module startup already composed the mediator, and a second call would double every
            // pipeline behaviour. Nothing on the JOB path carries [Requires]: a job has no HttpContext
            // and would be denied by its own authorization behaviour.
            services.AddScoped<IRequestHandler<GetHealthDashboardQuery, HealthDashboard>, GetHealthDashboardQueryHandler>();
            services.AddScoped<IRequestHandler<ListHealthRollupsQuery, IReadOnlyList<HealthRollupView>>, ListHealthRollupsQueryHandler>();
            services.AddScoped<IRequestHandler<AcknowledgeHealthAlertCommand, Unit>, AcknowledgeHealthAlertCommandHandler>();

            // Run-now: the only live probing besides the job, health:manage only, ONE in flight per site. The gate
            // is a singleton so every request scope of this process shares it.
            services.TryAddSingleton<HealthRunNowGate>();
            services.AddScoped<IRequestHandler<RunHealthProbesNowCommand, HealthRunNowResult>, RunHealthProbesNowCommandHandler>();

            // The module's server IService (dispatches via IMediator). Explicit AddTransient so it wins over the
            // client HTTP implementation on the server - the Guestbook / HtmlText pattern.
            services.AddTransient<Modules.Health.Services.IHealthService, Services.ServerHealthService>();
        }
    }
}
