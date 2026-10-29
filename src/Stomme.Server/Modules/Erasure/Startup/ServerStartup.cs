#nullable enable
using System;
using Stomme.Erasure;
using Stomme.Infrastructure;
using Stomme.Modules.Erasure.Application;
using Stomme.Modules.Erasure.Domain;
using Stomme.Modules.Erasure.Participants;
using Stomme.Erasure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Erasure.Startup
{
    /// <summary>
    /// The right-to-erasure module: the request/receipt aggregate on the storage-blind seam, the queue
    /// deleting a user calls, the runner, the per-site job, and the framework's own participants. Always composed -
    /// erasure is not a feature an install opts into. A module's participant lives with the module (Sessions,
    /// IdentityAudit, Notifications, Metering register theirs).
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
            // A clock the queue, the runner and the audit participant read; TryAdd, so the host or a test may supply its own.
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton(services, TimeProvider.System);
            services.AddModuleAggregatePersistence<ErasureRequestAggregate, Guid>();
            services.AddScoped<IErasureRequestRepository, ErasureRequestRepository>();
            services.AddScoped<IErasureQueue, ErasureQueue>();
            services.AddScoped<ErasureRunner>();

            // The receipts page's service on a Server-runtime site (the browser's HTTP twin is registered only in the
            // WebAssembly client's Program).
            services.AddScoped<Services.IErasureService, Services.ServerErasureService>();

            services.AddScoped<ISubjectErasureParticipant, UserSettingsErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, SiteNotificationsErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, VisitorsErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, LogsErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, PersonalPagesErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, SiteAccessErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, OpenIddictErasureParticipant>();
            services.AddScoped<ISubjectErasureParticipant, AuthorshipStampsErasureParticipant>(); // the author's username on every framework row
        }
    }
}
