#nullable enable
using System.Collections.Generic;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Metering;
using Stomme.Models.Metering;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Metering.Infrastructure;
using Stomme.Metering.Persistence;
using Stomme.Persistence.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System;

namespace Stomme.Modules.Metering.Startup
{
    /// <summary>
    /// Wires the metering module: four storage-blind aggregates, the recording port and the quota engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovered by the framework's assembly scan like every other module startup, so nothing in the composition
    /// root changes. Each aggregate takes ONE <c>AddModuleAggregatePersistence</c> call and the module authors no
    /// persistence code at all: on a relational install the host maps them into the shared context, on RavenDB or
    /// the File store they round-trip through the install's own store.
    /// </para>
    /// <para>
    /// The PORT is registered, never the implementation type: a producer takes <see cref="IUsageRecorder"/> from
    /// <c>Stomme.Shared</c> and never sees the metering domain. The fold gate takes the distributed lock when a
    /// provider registers one (PostgreSQL, RavenDB) and falls back to an in-process gate otherwise, which is a
    /// recorded divergence rather than a claim of cross-host safety.
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
            services.AddModuleAggregatePersistence<QuotaAggregate, Guid>();
            services.AddModuleAggregatePersistence<UsageRecordAggregate, Guid>();
            services.AddModuleAggregatePersistence<AiUsageRecordAggregate, Guid>();
            services.AddModuleAggregatePersistence<AiUsageRollupAggregate, Guid>();

            services.AddScoped<IQuotaRepository, QuotaRepository>();
            services.AddScoped<IUsageRecordRepository, UsageRecordRepository>();
            services.AddScoped<IAiUsageRecordRepository, AiUsageRecordRepository>();
            services.AddScoped<IAiUsageRollupRepository, AiUsageRollupRepository>();

            // CostOptimization's AI-usage read port: the module that holds the
            // AI ledger implements it, so the advisor never reads Metering's repositories.
            services.AddScoped<Stomme.CostOptimization.IAssistantUsageSignalSource, MeteringAssistantUsageSignalSource>();

            services.TryAddSingleton<IMeteringClock, SystemMeteringClock>();
            // The price list is read from the MERGED configuration (HostConfiguration) - a deployed install keeps it in the override file.
            services.TryAddSingleton(sp => new AiCostCalculator(HostConfiguration.Resolve(sp)));
            services.TryAddSingleton(sp => new MeteringDefaults());
            services.TryAddScoped<MeteringScope>();
            services.TryAddScoped<QuotaLimitResolver>();
            services.TryAddScoped<IMeteringSiteSettings, SettingRepositoryMeteringSiteSettings>();
            services.TryAddScoped(sp => new QuotaFold(sp.GetService<IDistributedLock>()));

            // Quotas on the boot/upgrade path: additive, create-if-absent per
            // site, run by the framework's SiteContentReconciliationRunner - never from a hosted service's
            // StartAsync, which has whichever tenant the ambient scope happened to hold. The module ships NO
            // IDeclaredQuotaSource: a stock install seeds nothing, and a later module that meters brings its own.
            services.AddScoped<ISiteContentReconciler, MeteringSiteReconciler>();

            // The nightly pass, resolved inside each site's own DI scope by UsageRollupJob. The two jobs themselves
            // are HostedServiceBase types found by the framework's assembly scan, never AddHostedService'd here.
            services.AddScoped<UsageRollupPass>();

            // Quota crossings reach the site's administrators through the notification producer port, and the two templates
            // this module needs join the per-site template reconciliation through its own source seam.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Modules.Notifications.Application.Templates.INotificationTemplateSource, MeteringTemplateSource>());
            services.TryAddScoped<QuotaCrossingNotifier>();

            // Erasure: when a user is removed from a site the ledger is pseudonymised, not deleted.
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, MeteringUserErasure>();

            // Retention when metering is OFF (BL-275): PurgeJob runs on every install, the rollup job only with Metering:Enabled,
            // so this pass takes the user id out of ledger rows past MeteringUsageRetention when nothing else would.
            services.AddScoped<Stomme.Infrastructure.IRetentionPurger, MeteringLedgerRetentionPurger>();

            // The ports, and only the ports - except UsageRecorder, whose concrete type the fold recovery job
            // resolves so a recovered fold runs through exactly the same lock, retry and Applied flip as a live one.
            services.TryAddScoped<UsageRecorder>();
            services.TryAddScoped<IUsageRecorder>(sp => sp.GetRequiredService<UsageRecorder>());
            services.TryAddScoped<IQuotaInspector, QuotaInspector>();
            services.TryAddScoped<IAiUsageRecorder, AiUsageRecorder>();

            // The USER-FACING CQRS surface: every request carries [Requires] with one of the three
            // keys, and none of them names a site, a tenant or a user. Handlers are registered EXPLICITLY - never
            // AddMediator()/AddMediatorBehavior() here: Guestbook's startup already composed the mediator, and a
            // second AddMediator would double every pipeline behavior. Nothing on the JOB path carries
            // [Requires]: a job has no HttpContext and would be denied by its own authorization behavior.
            services.AddScoped<IRequestHandler<ListQuotasQuery, IReadOnlyList<QuotaView>>, ListQuotasQueryHandler>();
            services.AddScoped<IRequestHandler<GetQuotaQuery, QuotaView>, GetQuotaQueryHandler>();
            services.AddScoped<IRequestHandler<ListUsageQuery, IReadOnlyList<UsageRecordView>>, ListUsageQueryHandler>();
            services.AddScoped<IRequestHandler<GetMyUsageQuery, IReadOnlyList<MyUsageLine>>, GetMyUsageQueryHandler>();
            services.AddScoped<IRequestHandler<GetAiUsageDashboardQuery, AiUsageDashboard>, GetAiUsageDashboardQueryHandler>();
            services.AddScoped<IRequestHandler<ListAiUsageQuery, IReadOnlyList<AiUsageRecordView>>, ListAiUsageQueryHandler>();

            services.AddScoped<IRequestHandler<DefineQuotaCommand, Guid>, DefineQuotaCommandHandler>();
            services.AddScoped<IRequestHandler<ResetQuotaCommand, Unit>, ResetQuotaCommandHandler>();
            services.AddScoped<IRequestHandler<GrantQuotaOverrideCommand, Unit>, GrantQuotaOverrideCommandHandler>();
            services.AddScoped<IRequestHandler<SetQuotaEnforcementCommand, Unit>, SetQuotaEnforcementCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteQuotaCommand, Unit>, DeleteQuotaCommandHandler>();

            // The module's server IService (dispatches via IMediator). Explicit AddTransient so it wins over the
            // client HTTP implementation on the server - the Guestbook / HtmlText pattern.
            services.AddTransient<Services.IMeteringService, Services.ServerMeteringService>();
        }
    }
}
