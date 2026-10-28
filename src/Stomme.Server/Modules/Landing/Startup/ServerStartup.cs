#nullable enable
using System;
using Stomme.Infrastructure;
using Stomme.Landing;
using Stomme.Landing.Persistence;
using Stomme.Mediator;
using Stomme.Modules.Landing.Application;
using Stomme.Modules.Landing.Domain;
using Stomme.Modules.Landing.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Landing.Startup
{
    /// <summary>
    /// The Landing module's composition (BL-98): the storage-blind page aggregate, its repository and its requests. The
    /// handlers are registered explicitly - an earlier startup composed the mediator, and a second AddMediator would double
    /// every pipeline behaviour (BL-78 F13).
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
            services.AddModuleAggregatePersistence<LandingPageAggregate, Guid>();
            services.AddScoped<ILandingPageRepository, LandingPageRepository>();

            services.AddScoped<IRequestHandler<GetLandingPageQuery, LandingPageView?>, GetLandingPageQueryHandler>();
            services.AddScoped<IRequestHandler<GetLandingTemplateQuery, PredefinedLandingContent>, GetLandingTemplateQueryHandler>();
            services.AddScoped<IRequestHandler<ConfigurePredefinedLandingCommand, LandingPageView>, ConfigurePredefinedLandingCommandHandler>();
            services.AddScoped<IRequestHandler<ConfigureDashboardLandingCommand, LandingPageView>, ConfigureDashboardLandingCommandHandler>();
            services.AddScoped<IRequestHandler<PublishLandingCommand, LandingPageView>, PublishLandingCommandHandler>();
            services.AddScoped<IRequestHandler<UnpublishLandingCommand, LandingPageView>, UnpublishLandingCommandHandler>();
            services.AddScoped<IRequestHandler<ResetLandingCommand, Unit>, ResetLandingCommandHandler>();

            // The AI mode and the edit mode (increments 3-4): the sanitizer, the requests, and a designer that says none is
            // composed - the AI assistant registers its Landing Page Designer over it.
            services.TryAddSingleton<Stomme.Modules.Landing.Infrastructure.LandingHtmlSanitizer>();
            services.TryAddScoped<ILandingPageDesigner, NoLandingPageDesigner>();
            services.AddScoped<IRequestHandler<GenerateLandingDraftCommand, LandingDraftView>, GenerateLandingDraftCommandHandler>();
            services.AddScoped<IRequestHandler<ProposeLandingSectionCommand, LandingDraftView>, ProposeLandingSectionCommandHandler>();
            services.AddScoped<IRequestHandler<ConfigureGeneratedLandingCommand, LandingPageView>, ConfigureGeneratedLandingCommandHandler>();

            // Last registration wins over the browser's HTTP service, which the host also composes for Blazor Server.
            services.AddTransient<ILandingService, ServerLandingService>();
        }
    }
}
