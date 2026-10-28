using System;
using System.Collections.Generic;
using Stomme.CostOptimization;
using Stomme.CostOptimization.Persistence;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Modules.CostOptimization.Application.Advisors;
using Stomme.Modules.CostOptimization.Domain;
using Stomme.Modules.CostOptimization.Services;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.CostOptimization.Startup
{
    /// <summary>
    /// The CostOptimization module's composition (BL-97 increment 6): the storage-blind, tenant-owned recommendation on
    /// whichever store the install chose (D79/D80), its closed repository, the advisor's options from the HOST's
    /// configuration (BL-162), the four requests, the page's service, and the predecessor platform's five advisor rules (increment 7), each
    /// reading its Shared read port - which CloudUsage and Metering implement - and drafting nothing when the port is not
    /// composed; and (increment 8) the model-tier router and the per-site response cache the AI assistant will consume, and
    /// the temp-file sweep. The weekly advisor job and the daily temp-file job are HostedServiceBase types found by the
    /// framework's assembly scan, never registered here.
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
            services.AddModuleAggregatePersistence<OptimizationRecommendationAggregate, Guid>();
            services.AddScoped<IOptimizationRecommendationRepository, OptimizationRecommendationRepository>();
            services.TryAddSingleton(sp => AdvisorOptions.From(HostConfiguration.Resolve(sp)));

            services.AddScoped<IRequestHandler<RaiseRecommendationsCommand, AdvisorRunView>, RaiseRecommendationsCommandHandler>();
            services.AddScoped<IRequestHandler<ApplyRecommendationCommand, RecommendationDecisionView>, ApplyRecommendationCommandHandler>();
            services.AddScoped<IRequestHandler<DismissRecommendationCommand, RecommendationDecisionView>, DismissRecommendationCommandHandler>();
            services.AddScoped<IRequestHandler<ListRecommendationsQuery, IReadOnlyList<RecommendationView>>, ListRecommendationsQueryHandler>();
            services.AddScoped<IValidator<ApplyRecommendationCommand>, ApplyRecommendationCommandValidator>();
            services.AddScoped<IValidator<DismissRecommendationCommand>, DismissRecommendationCommandValidator>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<ICostOptimizationService, ServerCostOptimizationService>();

            // The advisor's rules (increment 7), the predecessor platform's five.
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecommendationAnalyzer, ExpensiveModelForSimpleTaskAnalyzer>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecommendationAnalyzer, HighCacheMissAnalyzer>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecommendationAnalyzer, QuotaApproachingAnalyzer>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecommendationAnalyzer, CloudCostAnomalyAnalyzer>());
            services.TryAddEnumerable(ServiceDescriptor.Scoped<IRecommendationAnalyzer, LicensingSpendReviewAnalyzer>());

            // Increment 8: the router and the response cache (their options from the HOST's configuration), and the sweep the
            // default-off temp-file job runs.
            services.TryAddSingleton(sp => ModelTiersOptions.From(HostConfiguration.Resolve(sp)));
            services.TryAddSingleton<IModelTierRouter, RuleBasedModelTierRouter>();
            services.TryAddSingleton(sp => ResponseCacheOptions.From(HostConfiguration.Resolve(sp)));
            services.TryAddScoped<IModelResponseCache, CacheManagerModelResponseCache>();
            services.TryAddScoped<TempFileSweeper>();
        }
    }
}
