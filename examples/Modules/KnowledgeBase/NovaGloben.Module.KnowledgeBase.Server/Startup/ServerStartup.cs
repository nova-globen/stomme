using System;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Mcp.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaGloben.Module.KnowledgeBase.Application;
using NovaGloben.Module.KnowledgeBase.DataDuties;
using NovaGloben.Module.KnowledgeBase.Discovery;
using NovaGloben.Module.KnowledgeBase.Operations;
using Stomme.Infrastructure.DemoContent;
using NovaGloben.Module.KnowledgeBase.Domain;
using NovaGloben.Module.KnowledgeBase.Persistence;
using NovaGloben.Module.KnowledgeBase.Services;

namespace NovaGloben.Module.KnowledgeBase.Startup
{
    /// <summary>
    /// The module's server composition, found by the host's convention scan (an <see cref="IServerStartup"/> in a
    /// <c>*.Stomme</c> assembly). It names NO store: <c>AddModuleAggregatePersistence</c> puts each aggregate on whichever
    /// provider the installation chose - SQLite, PostgreSQL, SQL Server, RavenDB or the file store.
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

            // The two aggregates, storage-blind and tenant-owned.
            services.AddModuleAggregatePersistence<Article, Guid>();
            services.AddModuleAggregatePersistence<Category, Guid>();
            services.AddScoped<IArticleRepository, ArticleRepository>();
            services.AddScoped<ICategoryRepository, CategoryRepository>();

            // The commands and queries in this assembly.
            services.AddMediator<ServerStartup>();

            // The in-process reaction to a publish, and the notification template every site receives.
            services.AddScoped<Stomme.SharedKernel.Domain.IDomainEventHandler<ArticlePublishedEvent>, ArticlePublishedDomainEventHandler>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Stomme.Modules.Notifications.Application.Templates.INotificationTemplateSource, KnowledgeBaseTemplateSource>());

            // The discovery seams. The help topics and the feature flag are found by the host's convention, and
            // the server manager (search, sitemap) is created by the host from ModuleDefinition.ServerManagerType. The MCP
            // contribution needs the mediator, so it is registered here - and only while the host's MCP server half is
            // composed: with it off the host has no catalog, and a module must not contribute to one that does not exist.
            if (McpSettings.ServerEnabled(HostConfiguration.Resolve(services)))
            {
                services.AddScoped<Stomme.Mcp.IMcpContributor, KnowledgeBaseMcpContributor>();
            }

            // The data duties: the author's articles in their data package and in an erasure, and the
            // published articles as an export source. Module content export/import is the server manager's IPortable,
            // and a backup carries both aggregates with no code here at all - AddModuleAggregatePersistence registered them.
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, KnowledgeBaseSubjectDataContributor>();
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, KnowledgeBaseErasureParticipant>();
            services.TryAddEnumerable(ServiceDescriptor.Scoped<Stomme.Export.IExportSource, KnowledgeBaseArticlesExportSource>());

            // The operations: the stale-drafts probe on the host's health monitor, its threshold read per scope from
            // the host's configuration (the override file a deployed install writes); and the demo articles, composed
            // only while the host's demo switch is on at composition - off, nothing is seeded and nothing is registered.
            services.AddScoped<Stomme.Health.IHealthContributor>(sp => new KnowledgeBaseHealthContributor(
                sp.GetRequiredService<IArticleRepository>(),
                sp.GetRequiredService<TimeProvider>(),
                KnowledgeBaseSettings.StaleDraftDays(HostConfiguration.Resolve(sp))));
            if (DemoContentSettings.Enabled(HostConfiguration.Resolve(services)))
            {
                services.TryAddScoped<DemoContentStamp>();
                services.AddScoped<ISiteContentReconciler, KnowledgeBaseDemoContentReconciler>();
            }

            // Last registration wins over the client's HTTP service, which the host also composes for Blazor Server.
            services.AddTransient<IKnowledgeBaseService, ServerKnowledgeBaseService>();
        }
    }
}
