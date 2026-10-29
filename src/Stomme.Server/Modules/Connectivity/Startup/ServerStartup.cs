using System;
using System.Collections.Generic;
using Stomme.Connectivity;
using Stomme.Connectivity.Persistence;
using System.Linq;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Modules.Connectivity.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Connectivity.Application;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Modules.Connectivity.Health;
using Stomme.Modules.Connectivity.Services;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Connectivity.Startup
{
    /// <summary>
    /// The Connectivity module's server composition (BL-94 increments 5 and 6): the tenant-owned
    /// <see cref="ConnectionAggregate"/> on the storage-blind seam, the module's repository, the connection secrets
    /// over the per-site secret store, the test runner, the extraction port's implementation, and the CQRS handlers
    /// (registered EXPLICITLY - never AddMediator() here, the host composed the mediator once). The connector catalog
    /// and the egress factory are the host's (the composition root). The API and the page are increment 7.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        /// <summary>The host's configuration as the composition seam sees it (the same reading BL-162 fixed for readers with DI: the layered root when it is registered, else the builder's).</summary>
        private static Microsoft.Extensions.Configuration.IConfiguration? HostDataDirectoryConfiguration(IServiceCollection services) =>
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
            services.AddModuleAggregatePersistence<ConnectionAggregate, Guid>();
            services.AddScoped<IConnectionRepository, ConnectionRepository>();
            services.AddScoped<ConnectionSecrets>();
            services.AddScoped<ConnectionTestRunner>();
            // The health contribution (increment 11): one Site-scope probe over the site's connections, gated by
            // Connectivity:HealthProbes:Enabled (default OFF) read from THIS host's configuration - never IConfiguration.
            services.AddScoped<Stomme.Health.IHealthContributor>(sp => new ConnectionHealthContributor(
                ConnectivityHealthProbes.Enabled(HostConfiguration.Resolve(sp)),
                sp.GetRequiredService<IConnectionRepository>(),
                sp.GetRequiredService<Stomme.SharedKernel.Domain.IUnitOfWork>(),
                sp.GetRequiredService<ConnectionTestRunner>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConnectionHealthContributor>>()));
            services.AddScoped<IConnectorExtractionService, ConnectorExtractionService>();
            services.AddScoped<IConnectorFileSink, SiteFolderConnectorFileSink>(); // A connector's files land in a site folder through the ingestion guard

            services.AddScoped<IRequestHandler<CreateConnectionCommand, Guid>, CreateConnectionCommandHandler>();
            services.AddScoped<IRequestHandler<UpdateConnectionCommand, Unit>, UpdateConnectionCommandHandler>();
            services.AddScoped<IRequestHandler<TestConnectionCommand, ConnectionTestView>, TestConnectionCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteConnectionCommand, Unit>, DeleteConnectionCommandHandler>();
            services.AddScoped<IRequestHandler<ListConnectionsQuery, IReadOnlyList<ConnectionView>>, ListConnectionsQueryHandler>();
            services.AddScoped<IRequestHandler<GetConnectionQuery, ConnectionView>, GetConnectionQueryHandler>();
            services.AddScoped<IRequestHandler<ListConnectorsQuery, IReadOnlyList<ConnectorView>>, ListConnectorsQueryHandler>();
            // The site's egress allowlist: read by a holder of connections:view, saved by the Host role alone.
            services.TryAddScoped<IConnectivityEgressSettings, SettingRepositoryConnectivityEgressSettings>();
            services.AddScoped<IRequestHandler<GetConnectionEgressQuery, ConnectionEgressView>, GetConnectionEgressQueryHandler>();
            services.AddScoped<IRequestHandler<SaveConnectionEgressCommand, ConnectionEgressView>, SaveConnectionEgressCommandHandler>();
            services.AddScoped<IValidator<CreateConnectionCommand>, CreateConnectionCommandValidator>();
            services.AddScoped<IValidator<UpdateConnectionCommand>, UpdateConnectionCommandValidator>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<IConnectionsService, ServerConnectionsService>();

            // Demo content (increment 14): two connections per site on the boot path, ONLY while DemoContent:Enabled is
            // true (default off) - while it is off the reconciler is not even registered, so a stock install is untouched.
            if (DemoContentSettings.Enabled(HostDataDirectoryConfiguration(services)))
            {
                services.TryAddScoped<DemoContentStamp>();
                services.AddScoped<ISiteContentReconciler, ConnectionsDemoContentReconciler>();
            }
        }
    }
}
