using System;
using Stomme.Infrastructure;
using Stomme.Modules.IdentityAudit.Application;
using Stomme.Modules.IdentityAudit.Domain;
using Stomme.IdentityAudit.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.IdentityAudit.Startup
{
    /// <summary>
    /// Wires the identity audit ledger (BL-120): one storage-blind, tenant-owned aggregate, its recorder, its retention
    /// pass and the server side of its page. The WRITE points are the framework's own identity code (<c>UserManager</c>,
    /// the login page, <c>UserRoleController</c>), which resolve <see cref="IIdentityAuditRecorder"/> optionally.
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
            services.AddModuleAggregatePersistence<IdentityAuditEntryAggregate, Guid>();
            services.AddScoped<IIdentityAuditRepository, IdentityAuditRepository>();
            services.AddScoped<IIdentityAuditRecorder, IdentityAuditRecorder>();
            services.AddScoped<IRetentionPurger, Infrastructure.IdentityAuditRetentionPurger>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, IdentityAuditSubjectContributor>(); // BL-82: the lines about a person, in their data package
            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, IdentityAuditErasureParticipant>(); // D112: retained one year as a security record, then deleted

            // Wins over the client HTTP implementation on the server (the Health / Guestbook pattern).
            services.AddTransient<Services.IIdentityAuditService, Services.ServerIdentityAuditService>();
        }
    }
}
