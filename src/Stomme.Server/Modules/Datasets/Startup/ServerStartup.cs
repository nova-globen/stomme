using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Datasets;
using Stomme.Datasets.Persistence;
using Stomme.Infrastructure;
using Stomme.Infrastructure.DemoContent;
using Stomme.Infrastructure.Governance;
using Stomme.Modules.Datasets.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Domain;
using Stomme.Modules.Datasets.Services;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Datasets.Startup
{
    /// <summary>
    /// The Datasets module's server composition (BL-94 increments 9, 10 and 13): the tenant-owned
    /// <see cref="DatasetAggregate"/> on the storage-blind seam, the module's repository, the Shared read and write ports'
    /// implementations, the PII scanner and the classifier over it, the CQRS handlers (registered EXPLICITLY - never
    /// AddMediator() here), the validators, the managed-file reader and the upload source (13), and the client contract's server twin.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        /// <summary>The host's configuration as the composition seam sees it (the layered root when it is registered, else the builder's - BL-162's reading, before DI exists).</summary>
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
            services.AddModuleAggregatePersistence<DatasetAggregate, Guid>();
            services.AddScoped<IDatasetRepository, DatasetRepository>();
            services.AddScoped<IDatasetCatalog, DatasetCatalog>();
            services.AddScoped<IDatasetRegistrar, DatasetRegistrar>();
            services.TryAddSingleton<IPiiScanner, PiiScanner>();
            services.AddScoped<IDataClassifier, GovernanceDataClassifier>();

            services.AddScoped<IManagedFileReader, ManagedFileReader>();
            services.AddScoped<IUploadedDatasetSource, ManagedFileUploadedDatasetSource>();

            services.AddScoped<IRequestHandler<RegisterDatasetCommand, Guid>, RegisterDatasetCommandHandler>();
            services.AddScoped<IRequestHandler<RegisterUploadedDatasetCommand, Guid>, RegisterUploadedDatasetCommandHandler>();
            services.AddScoped<IRequestHandler<RefreshDatasetCommand, Unit>, RefreshDatasetCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteDatasetCommand, Unit>, DeleteDatasetCommandHandler>();
            services.AddScoped<IRequestHandler<ListDatasetsQuery, IReadOnlyList<DatasetDescriptor>>, ListDatasetsQueryHandler>();
            services.AddScoped<IRequestHandler<GetDatasetQuery, DatasetDescriptor>, GetDatasetQueryHandler>();
            services.AddScoped<IRequestHandler<PreviewDatasetRowsQuery, DatasetPreviewRows>, PreviewDatasetRowsQueryHandler>();
            services.AddScoped<IValidator<RegisterDatasetCommand>, RegisterDatasetCommandValidator>();
            services.AddScoped<IValidator<RegisterUploadedDatasetCommand>, RegisterUploadedDatasetCommandValidator>();
            services.AddScoped<IValidator<RefreshDatasetCommand>, RefreshDatasetCommandValidator>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<IDatasetsService, ServerDatasetsService>();

            // The export half: a dataset's rows as an export source; the engine checks datasets:preview.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, DatasetRowsExportSource>());

            // Demo content (increment 14): three datasets per site on the boot path, ONLY while DemoContent:Enabled is
            // true (default off) - while it is off the reconciler is not even registered, so a stock install is untouched.
            if (DemoContentSettings.Enabled(HostCompositionConfiguration(services)))
            {
                services.TryAddScoped<DemoContentStamp>();
                services.AddScoped<ISiteContentReconciler, DatasetsDemoContentReconciler>();
            }

            // The MCP surface (increment 12): needs the mediator, so registered here rather than discovered; its flag is
            // discovered. Registered ONLY while the MCP server half is composed (Mcp:Server:Enabled, read at composition
            // from the host's root): with the server half off there is no catalog at all, and no contributor either
            // (McpCatalogDriveTests pins that for every module).
            if (Stomme.Modules.Mcp.Startup.McpSettings.ServerEnabled(HostCompositionConfiguration(services)))
            {
                services.AddScoped<Stomme.Mcp.IMcpContributor, Mcp.DatasetsMcpContributor>();
            }
        }
    }
}
