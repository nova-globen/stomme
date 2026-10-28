#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Collaboration;
using Stomme.Collaboration.Persistence;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Modules.Collaboration.Infrastructure;
using Stomme.Modules.Collaboration.Realtime;
using Stomme.SharedKernel.Domain;
using Stomme.Transport;
using FluentValidation;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Stomme.Modules.Collaboration.Startup
{
    /// <summary>
    /// Wires the collaboration seam (BL-97 increment 11, D114): the lock and the session (storage-blind, tenant-owned), the
    /// services over them, the gated requests, <c>api/Collaboration</c> and the <c>admin/collaboration</c> overview, the
    /// sweep job, and the subject-rights participants. The persistence is registered UNCONDITIONALLY - a switch turned off
    /// must not take the data with it (the Help module's rule); the requests and the API answer only while
    /// <c>Collaboration:Enabled</c> is on, so with it off editing is exactly the single-editor behaviour (D114). The presence
    /// and change feed over the hub, and the editors' wiring, are increment 12.
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
            services.AddModuleAggregatePersistence<ResourceLockAggregate, Guid>();
            services.AddModuleAggregatePersistence<EditSessionAggregate, Guid>();
            services.AddScoped<IResourceLockRepository, ResourceLockRepository>();
            services.AddScoped<IEditSessionRepository, EditSessionRepository>();
            services.TryAddSingleton(TimeProvider.System);

            // Collaboration (D114: off by default) - from THIS host's configuration, the override file included (BL-162).
            services.TryAddSingleton(provider =>
            {
                var options = new CollaborationOptions();
                HostConfiguration.Resolve(provider)?.GetSection(CollaborationOptions.SectionName).Bind(options);
                return options;
            });

            services.TryAddSingleton<CollaborationGate>();
            services.TryAddSingleton<IConflictResolver, ConflictResolver>();
            services.TryAddScoped<ICollaborationCaller, CollaborationCaller>();
            services.TryAddScoped<ICollaborativeResourceAccess, ModuleCollaborativeResourceAccess>();
            services.TryAddScoped<ResourceLockService>();
            services.TryAddScoped<EditSessionService>();
            services.TryAddScoped<CollaborationSweeper>();
            services.TryAddScoped<PresenceSweeper>();

            services.AddScoped<IRequestHandler<AcquireLockCommand, ResourceLockResult>, AcquireLockCommandHandler>();
            services.AddScoped<IRequestHandler<RenewLockCommand, ResourceLockResult?>, RenewLockCommandHandler>();
            services.AddScoped<IRequestHandler<ReleaseLockCommand, Unit>, ReleaseLockCommandHandler>();
            services.AddScoped<IRequestHandler<ForceReleaseLockCommand, bool>, ForceReleaseLockCommandHandler>();
            services.AddScoped<IRequestHandler<GetLockQuery, ResourceLockResult?>, GetLockQueryHandler>();
            services.AddScoped<IRequestHandler<JoinEditSessionCommand, EditSessionView>, JoinEditSessionCommandHandler>();
            services.AddScoped<IRequestHandler<UpdatePresenceCommand, Unit>, UpdatePresenceCommandHandler>();
            services.AddScoped<IRequestHandler<BroadcastChangeCommand, EditSessionView>, BroadcastChangeCommandHandler>();
            services.AddScoped<IRequestHandler<HeartbeatEditSessionCommand, Unit>, HeartbeatEditSessionCommandHandler>();
            services.AddScoped<IRequestHandler<LeaveEditSessionCommand, Unit>, LeaveEditSessionCommandHandler>();
            services.AddScoped<IRequestHandler<CloseEditSessionCommand, Unit>, CloseEditSessionCommandHandler>();
            services.AddScoped<IRequestHandler<GetEditSessionQuery, EditSessionView?>, GetEditSessionQueryHandler>();
            services.AddScoped<IRequestHandler<GetOpenEditSessionQuery, EditSessionView?>, GetOpenEditSessionQueryHandler>();
            services.AddScoped<IRequestHandler<GetCollaborationOverviewQuery, CollaborationOverviewView>, GetCollaborationOverviewQueryHandler>();
            services.AddScoped<IValidator<AcquireLockCommand>, AcquireLockCommandValidator>();
            services.AddScoped<IValidator<RenewLockCommand>, RenewLockCommandValidator>();
            services.AddScoped<IValidator<BroadcastChangeCommand>, BroadcastChangeCommandValidator>();

            // Explicit AddTransient so it wins over the client's HTTP implementation on the server (the Guestbook pattern).
            services.AddTransient<Stomme.Modules.Collaboration.Services.ICollaborationService, Services.ServerCollaborationService>();

            // Presence and the change feed (increment 12): the aggregates' domain events and a board stored by the Dashboards module
            // become one integration event whose handler tells this host's circuits (the signal) and the site's hub group. The push
            // is the SignalR adapter only while the transport is on (resolved at runtime, so a host with it off validates and
            // boots); a no-op otherwise, and the editor polls.
            services.TryAddSingleton<ICollaborationSignal, CollaborationSignal>();
            services.TryAddScoped<ICollaborationNotifier, CollaborationNotifier>();
            services.TryAddSingleton<ICollaborationRealtimePush>(sp => sp.GetService<TransportOptions>()?.SignalR.Enabled == true
                ? new CollaborationRealtimePush(sp.GetRequiredService<IHubContext<CollaborationHub>>())
                : new NoCollaborationRealtimePush());
            services.AddScoped<IDomainEventHandler<LockAcquiredEvent>, CollaborationDomainSignal>();
            services.AddScoped<IDomainEventHandler<LockReleasedEvent>, CollaborationDomainSignal>();
            services.AddScoped<IDomainEventHandler<PresenceChangedEvent>, CollaborationDomainSignal>();
            services.AddScoped<IDomainEventHandler<ChangeBroadcastEvent>, CollaborationDomainSignal>();
            services.AddScoped<IDomainEventHandler<Stomme.Modules.Dashboards.Domain.DashboardUpdatedEvent>, CollaborationDomainSignal>();
            services.AddScoped<IDomainEventHandler<Stomme.Modules.Dashboards.Domain.WidgetAddedEvent>, CollaborationDomainSignal>();
            services.AddScoped<IDomainEventHandler<Stomme.Modules.Dashboards.Domain.DashboardPublishedEvent>, CollaborationDomainSignal>();

            services.AddScoped<Stomme.Erasure.ISubjectErasureParticipant, CollaborationErasure>();
            services.AddScoped<Stomme.Portability.ISubjectDataContributor, CollaborationSubjectContributor>();
        }
    }
}
