using System;
using Stomme.Infrastructure;
using Stomme.Modules.Portability.Application;
using Stomme.Modules.Portability.Application.Format;
using Stomme.Modules.Portability.Domain;
using Stomme.Modules.Portability.Execution;
using Stomme.Portability.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Portability.Startup
{
    /// <summary>
    /// Wires data portability: the package and audit aggregates (storage-blind, tenant-owned), the format,
    /// the contributor registry, the validate-first importer and the exporter. Contributors register themselves - scoped,
    /// explicitly, from their own module's startup.
    /// </summary>
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
            services.AddModuleAggregatePersistence<PortabilityPackageAggregate, Guid>();
            services.AddModuleAggregatePersistence<PortabilityAuditEntryAggregate, Guid>();
            services.AddScoped<IPortabilityPackageRepository, PortabilityPackageRepository>();
            services.AddScoped<IPortabilityAuditRepository, PortabilityAuditRepository>();
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, Application.PortabilityErasureParticipant>(); // The subject's packages go; the accountability rows are retained one year

            services.AddSingleton<PortabilityBundleSerializer>();
            services.AddScoped<PortabilityContributorRegistry>();
            services.AddScoped<PortabilityImporter>();
            services.AddScoped<PortabilityExporter>();

            services.TryAddSingleton(provider =>
            {
                // IConfigurationRoot FIRST (HostConfiguration.Resolve): the plain IConfiguration lacked the override file a
                // deployed install writes its switches into (it has it now, but still lacks features.json).
                var options = new PortabilityOptions();
                HostConfiguration.Resolve(provider)?.GetSection(PortabilityOptions.SectionName).Bind(options);
                return options;
            });
            services.AddScoped<PortabilityFolder>();
            services.AddScoped<PortabilityArtifactStore>();
            services.AddScoped<PortabilityBuilder>();
            services.AddScoped<IRetentionPurger, PortabilityRetentionPurger>();

            // The controller calls the server service directly; it also wins over the client HTTP implementation on the server.
            services.AddScoped<Services.ServerPortabilityService>();
            services.AddScoped<Services.IPortabilityService>(sp => sp.GetRequiredService<Services.ServerPortabilityService>());

            // The shipped contributors - scoped, explicit. Other modules register their own the same way.
            services.AddScoped<Stomme.Portability.IPortableEntityContributor, Contributions.ProfileDefinitionContributor>();
            services.AddScoped<Stomme.Portability.IPortableEntityContributor, Contributions.ModuleContentContributor>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, Contributions.UserAccountSubjectContributor>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, Contributions.UserSettingsSubjectContributor>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, Contributions.SiteRolesSubjectContributor>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, Contributions.NotificationMetadataSubjectContributor>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, Contributions.PersonalPagesSubjectContributor>();        }
    }
}
