using System;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NovaGloben.Module.Blogs.Domain;
using NovaGloben.Module.Blogs.Persistence;
using NovaGloben.Module.Blogs.Services;

namespace NovaGloben.Module.Blogs.Startup
{
    /// <summary>
    /// Wires the Blogs module into the host. It is discovered by the framework's reflection scan the same way
    /// every module's startup is, so the module's CQRS/DDD stack plugs in by convention rather than by editing
    /// the framework composition root.
    /// </summary>
    public class ServerStartup : IServerStartup
    {
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // no middleware
        }

        public void ConfigureMvc(IMvcBuilder mvcBuilder)
        {
            // no MVC customization — the BlogController is picked up as an application part by the framework's
            // AddStommeApplicationParts scan over loaded module assemblies.
        }

        public void ConfigureServices(IServiceCollection services)
        {
            // ---- STORAGE-BLIND persistence (owner's bar #2) ------------------------------------------------
            // ONE call gives a working IRepository<BlogPost, Guid> on WHATEVER provider the install selected —
            // relational EF (the aggregate is mapped into the host's shared ModuleAggregatesDbContext, the
            // PostTitle value object becomes a plain string column by the IStringValueObject convention),
            // RavenDB, or the file store. The module authors ZERO EF/DbContext/store/provider-branch code. The
            // helper also wires the shared clean-arch pieces (aggregate tracker / domain-event dispatcher / unit
            // of work) and, on a relational install, an EnsureCreated schema initializer that creates the
            // BlogPost table at startup.
            services.AddModuleAggregatePersistence<BlogPost, Guid>();

            // The module's closed repository (provider-neutral: GetByModuleAsync filters via FindAsync on an int
            // equality). A CLOSED registration outside Stomme.Modules.*, so it neither collides with the
            // framework's open generic nor trips the reflective seam-store parity gate.
            services.AddScoped<IBlogPostRepository, BlogPostRepository>();

            // ---- CQRS wiring -------------------------------------------------------------------------------
            // AddMediator<T> registers IMediator (idempotently — re-registration is harmless) and scans THIS
            // module's assembly for its IRequestHandler / INotificationHandler / IStreamRequestHandler
            // implementations. The behavior PIPELINE (exception/logging/performance/validation) is contributed
            // once by the host and applies to every command, so this module deliberately registers no behaviors
            // (avoiding double-wrapping).
            services.AddMediator<ServerStartup>();

            // The framework domain-event handler, dispatched by the unit of work, coexisting with the mediator
            // notification handler discovered by AddMediator above — the two in-process fan-out paths.
            services.AddScoped<
                Stomme.SharedKernel.Domain.IDomainEventHandler<BlogPostPublishedDomainEvent>,
                Application.BlogPostPublishedDomainEventHandler>();

            // ---- The module's server service (dispatches via IMediator) ------------------------------------
            // Explicit AddTransient so it wins over the client's HTTP implementation on the server.
            services.AddTransient<IBlogService, ServerBlogService>();
        }
    }
}
