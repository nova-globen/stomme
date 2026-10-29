using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Dashboards;
using Stomme.Dashboards.Persistence;
using Stomme.Datasets;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Dashboards.Application;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.Dashboards.Infrastructure;
using Stomme.Modules.Dashboards.Realtime;
using Stomme.Modules.Datasets.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Transport;
using Microsoft.AspNetCore.SignalR;
using Stomme.Modules.Dashboards.Services;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Dashboards.Startup
{
    /// <summary>
    /// The Dashboards module's composition: the storage-blind, tenant-owned aggregate on
    /// whichever store the install chose, its closed repository, the commands and queries, the Datasets delete
    /// guard the boards answer, the page's service, and the real-time signal over the bus and the opt-in hub.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
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
            services.AddModuleAggregatePersistence<DashboardAggregate, Guid>();
            services.AddScoped<IDashboardRepository, DashboardRepository>();

            services.AddScoped<IRequestHandler<CreateDashboardCommand, Guid>, CreateDashboardCommandHandler>();
            services.AddScoped<IRequestHandler<UpdateDashboardCommand, Unit>, UpdateDashboardCommandHandler>();
            services.AddScoped<IRequestHandler<CloneDashboardCommand, Guid>, CloneDashboardCommandHandler>();
            services.AddScoped<IRequestHandler<PublishDashboardCommand, Unit>, PublishDashboardCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteDashboardCommand, Unit>, DeleteDashboardCommandHandler>();
            services.AddScoped<IRequestHandler<AddWidgetCommand, Guid>, AddWidgetCommandHandler>();
            services.AddScoped<IRequestHandler<UpdateWidgetCommand, Unit>, UpdateWidgetCommandHandler>();
            services.AddScoped<IRequestHandler<MoveWidgetCommand, Unit>, MoveWidgetCommandHandler>();
            services.AddScoped<IRequestHandler<RemoveWidgetCommand, Unit>, RemoveWidgetCommandHandler>();
            services.AddScoped<IRequestHandler<RearrangeLayoutCommand, Unit>, RearrangeLayoutCommandHandler>();
            services.AddScoped<IRequestHandler<SaveGridStateCommand, Unit>, SaveGridStateCommandHandler>();
            services.AddScoped<IRequestHandler<ReorderWidgetCommand, Unit>, ReorderWidgetCommandHandler>();
            services.AddScoped<IRequestHandler<ListDashboardsQuery, IReadOnlyList<DashboardView>>, ListDashboardsQueryHandler>();
            services.AddScoped<IRequestHandler<GetDashboardQuery, DashboardView>, GetDashboardQueryHandler>();
            services.AddScoped<IRequestHandler<GetWidgetDataQuery, WidgetDataView>, GetWidgetDataQueryHandler>();
            services.AddScoped<IValidator<CreateDashboardCommand>, CreateDashboardCommandValidator>();
            services.AddScoped<IValidator<UpdateDashboardCommand>, UpdateDashboardCommandValidator>();
            services.AddScoped<IValidator<CloneDashboardCommand>, CloneDashboardCommandValidator>();
            services.AddScoped<IValidator<AddWidgetCommand>, AddWidgetCommandValidator>();
            services.AddScoped<IValidator<UpdateWidgetCommand>, UpdateWidgetCommandValidator>();

            // The Datasets delete guard: a dataset a widget binds cannot be deleted while the widget stands.
            services.AddScoped<IDatasetUsageProvider, DashboardDatasetUsageProvider>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<IDashboardsService, ServerDashboardsService>();

            // The export half: one grid widget, and a whole board as one table per grid widget.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, DashboardGridExportSource>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, DashboardReportExportSource>());

            // Real-time refresh: a dataset's refresh or schema change, raised in the request's scope, becomes one
            // integration event whose handler pushes to the site's hub group. The push is the SignalR adapter only while the
            // transport is on (resolved at runtime, so a host with it off validates and boots); a no-op otherwise, and the page polls.
            services.TryAddSingleton<IDashboardDataSignal, DashboardDataSignal>();
            services.AddScoped<IDomainEventHandler<DatasetRefreshedEvent>, DatasetChangeSignal>();
            services.AddScoped<IDomainEventHandler<DatasetSchemaChangedEvent>, DatasetChangeSignal>();
            services.TryAddSingleton<IDashboardRealtimePush>(sp => sp.GetService<TransportOptions>()?.SignalR.Enabled == true
                ? new DashboardRealtimePush(sp.GetRequiredService<IHubContext<DashboardHub>>())
                : new NoDashboardRealtimePush());

            // Demo content: one published board over the demo datasets per site on the boot path, ONLY while
            // DemoContent:Enabled is true (default off) - while it is off the reconciler is not even registered.
            if (DemoContentSettings.Enabled(HostCompositionConfiguration(services)))
            {
                services.TryAddScoped<DemoContentStamp>();
                services.AddScoped<ISiteContentReconciler, DashboardsDemoContentReconciler>();
            }

            // The MCP surface: needs the mediator, so registered here rather than discovered; its flag is
            // discovered. Registered ONLY while the MCP server half is composed (Mcp:Server:Enabled, read at composition
            // from the host's root), as the Datasets contributor is.
            if (Stomme.Modules.Mcp.Startup.McpSettings.ServerEnabled(HostCompositionConfiguration(services)))
            {
                services.AddScoped<Stomme.Mcp.IMcpContributor, Mcp.DashboardsMcpContributor>();
            }
        }
    }
}
