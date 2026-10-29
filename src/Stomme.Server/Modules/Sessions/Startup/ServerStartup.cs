using System;
using Stomme.Infrastructure;
using Stomme.Modules.Sessions.Application;
using Stomme.Modules.Sessions.Domain;
using Stomme.Sessions.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Sessions.Startup
{
    /// <summary>
    /// Wires server-side sessions: one storage-blind, tenant-owned aggregate, its registry, its retention pass
    /// and the server side of its pages. The cookie's own events (<c>SessionCookieEvents</c>) and the per-request check
    /// (<c>PrincipalValidator</c>) are the framework's, and resolve <see cref="ISessionRegistry"/> from the request.
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
            services.AddModuleAggregatePersistence<UserSessionAggregate, Guid>();
            services.AddScoped<IUserSessionRepository, UserSessionRepository>();
            services.AddScoped<ISessionRegistry, SessionRegistry>();
            services.AddScoped<IRetentionPurger, Infrastructure.SessionRetentionPurger>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, SessionSubjectContributor>();
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, SessionErasureParticipant>(); // The subject's sessions go // BL-82: a person's sessions in their data package

            // The controller calls the server service directly; and it wins over the client HTTP implementation on the
            // server (the Health / Guestbook pattern).
            services.AddScoped<Services.ServerSessionService>();
            services.AddScoped<Services.ISessionService>(sp => sp.GetRequiredService<Services.ServerSessionService>());
        }
    }
}
