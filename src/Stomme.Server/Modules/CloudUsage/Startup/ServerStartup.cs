using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.CloudUsage;
using Stomme.CloudUsage.Persistence;
using Stomme.CostOptimization;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Mediator;
using Stomme.Modules.CloudUsage.Application;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Modules.CloudUsage.Infrastructure;
using Stomme.Modules.CloudUsage.Providers;
using Stomme.Modules.CloudUsage.Services;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.CloudUsage.Startup
{
    /// <summary>
    /// The CloudUsage module's composition (BL-97 increments 2 to 5): the three storage-blind, tenant-owned aggregates on
    /// whichever store the install chose (D79/D80), their closed repositories, the cost normaliser, the per-source cost
    /// adapters over the connection-backed payload source, the connection resolver over Connectivity, the sync, the
    /// commands and queries, the alert service with its two notification templates, the page's service, CloudUsage's side
    /// of CostOptimization's cloud-cost read port, and the demo board on the demo-content switch. The daily
    /// alert job is a HostedServiceBase found by the framework's assembly scan, never registered here.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        private static readonly CloudProvider[] SaasProviders =
        {
            CloudProvider.Datadog, CloudProvider.Snowflake, CloudProvider.GitHub, CloudProvider.Microsoft365, CloudProvider.Salesforce, CloudProvider.GenericRest,
        };

        private static Microsoft.Extensions.Configuration.IConfiguration? HostCompositionConfiguration(IServiceCollection services) =>
            services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Configuration.IConfigurationRoot))?.ImplementationInstance as Microsoft.Extensions.Configuration.IConfiguration
            ?? services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Configuration.IConfiguration))?.ImplementationInstance as Microsoft.Extensions.Configuration.IConfiguration;

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddModuleAggregatePersistence<CloudCostRecordAggregate, Guid>();
            services.AddModuleAggregatePersistence<CloudBudgetAggregate, Guid>();
            services.AddModuleAggregatePersistence<CloudCostAlertMarkerAggregate, Guid>();
            services.AddScoped<ICloudCostRecordRepository, CloudCostRecordRepository>();
            services.AddScoped<ICloudBudgetRepository, CloudBudgetRepository>();
            services.AddScoped<ICloudCostAlertMarkerRepository, CloudCostAlertMarkerRepository>();
            services.TryAddSingleton<ICloudCostNormalizer, CloudCostNormalizer>();

            // The adapters (increment 3): the three clouds' native payloads, and one SaaS/generic-REST adapter per source,
            // all over the connection-backed payload source - which reaches the network only through the query's host egress.
            services.TryAddSingleton<ICloudCostPayloadSource, ConnectorCostPayloadSource>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICloudCostProvider, AzureCostProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICloudCostProvider, AwsCostProvider>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICloudCostProvider, GcpCostProvider>());
            foreach (var saas in SaasProviders)
            {
                var provider = saas;
                services.AddSingleton<ICloudCostProvider>(sp => new SaasCostProvider(provider, sp.GetRequiredService<ICloudCostPayloadSource>(), sp.GetRequiredService<ICloudCostNormalizer>()));
            }

            services.TryAddSingleton<ICloudCostProviderRegistry, CloudCostProviderRegistry>();
            services.AddScoped<ICloudCostConnectionResolver, CloudCostConnectionResolver>();

            // The application (increment 4): the one sync path, the commands and queries, the alerts and their templates.
            services.AddScoped<ICloudCostSyncService, CloudCostSyncService>();
            services.AddScoped<IRequestHandler<SyncCloudCostsCommand, CloudCostSyncView>, SyncCloudCostsCommandHandler>();
            services.AddScoped<IRequestHandler<SetCloudBudgetCommand, CloudBudgetView>, SetCloudBudgetCommandHandler>();
            services.AddScoped<IRequestHandler<ListCloudBudgetsQuery, IReadOnlyList<CloudBudgetView>>, ListCloudBudgetsQueryHandler>();
            services.AddScoped<IRequestHandler<GetCloudCostSummaryQuery, CloudCostSummaryView>, GetCloudCostSummaryQueryHandler>();
            services.AddScoped<IRequestHandler<GetCloudCostByProviderQuery, CloudCostByProviderView>, GetCloudCostByProviderQueryHandler>();
            services.AddScoped<IRequestHandler<GetCloudCostByServiceQuery, CloudCostByServiceView>, GetCloudCostByServiceQueryHandler>();
            services.AddScoped<IRequestHandler<GetCloudCostMonthOverMonthQuery, CloudCostMonthOverMonthView>, GetCloudCostMonthOverMonthQueryHandler>();
            services.AddScoped<IRequestHandler<GetCloudCostBudgetStatusQuery, CloudBudgetStatusView>, GetCloudCostBudgetStatusQueryHandler>();
            services.AddScoped<IValidator<SetCloudBudgetCommand>, SetCloudBudgetCommandValidator>();
            services.AddScoped<CloudCostAlertService>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Modules.Notifications.Application.Templates.INotificationTemplateSource, CloudUsageTemplateSource>());

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<ICloudUsageService, ServerCloudUsageService>();

            // CostOptimization's cloud-cost read port (increment 5): the module that holds the ledger implements it.
            services.AddScoped<ICloudCostSignalSource, CloudUsageCloudCostSignalSource>();

            // The "Cloud Cost Analytics" board (increment 5): per site on the boot path, ONLY while DemoContent:Enabled is true
            // (default off) - while it is off the reconciler is not even registered, so a stock install is untouched.
            if (DemoContentSettings.Enabled(HostCompositionConfiguration(services)))
            {
                services.TryAddScoped<DemoContentStamp>();
                services.AddScoped<ISiteContentReconciler, CloudUsageDemoContentReconciler>();
            }
        }
    }
}
