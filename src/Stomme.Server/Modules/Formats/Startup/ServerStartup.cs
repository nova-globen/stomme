using System;
using System.Collections.Generic;
using Stomme.Formats;
using Stomme.Formats.Persistence;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Formats.Application;
using Stomme.Modules.Formats.Domain;
using Stomme.Modules.Formats.Services;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Formats.Startup
{
    /// <summary>
    /// The Formats module's server composition (BL-94, D110): the tenant-owned <see cref="FormatDefinitionAggregate"/>
    /// on the storage-blind seam (no DbContext, no RavenDB twin - the host binds the install's provider), the module's
    /// repository, the Shared port's server implementation, the CQRS handlers (registered EXPLICITLY - never
    /// AddMediator() here, the host composed the mediator once) and the client contract's server twin.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddModuleAggregatePersistence<FormatDefinitionAggregate, Guid>();
            services.AddScoped<IFormatDefinitionRepository, FormatDefinitionRepository>();
            services.AddScoped<IFormatService, SiteFormatService>();
            // The D108 cell formatter (BL-94 increment 8): a later registration wins a single resolve, and DataExport's own is a TryAdd.
            services.AddScoped<Stomme.Export.IExportCellFormatter, FormatsExportCellFormatter>();

            services.AddScoped<IRequestHandler<SaveFormatDefinitionCommand, Guid>, SaveFormatDefinitionCommandHandler>();
            services.AddScoped<IRequestHandler<DeleteFormatDefinitionCommand, Unit>, DeleteFormatDefinitionCommandHandler>();
            services.AddScoped<IRequestHandler<ListFormatsQuery, IReadOnlyList<FormatDescriptor>>, ListFormatsQueryHandler>();
            services.AddScoped<IRequestHandler<GetFormatQuery, FormatDescriptor?>, GetFormatQueryHandler>();
            services.AddScoped<IValidator<SaveFormatDefinitionCommand>, SaveFormatDefinitionCommandValidator>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<IFormatsService, ServerFormatsService>();
        }
    }
}
