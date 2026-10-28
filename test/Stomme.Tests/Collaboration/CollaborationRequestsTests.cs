#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Modules.Collaboration.Infrastructure;
using Stomme.Security;
using Stomme.Security.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Stomme.Tests.Collaboration.CollaborationRig;

namespace Stomme.Tests.Collaboration
{
    /// <summary>
    /// BL-97 increment 11 - the predecessor platform's <c>LockCqrsTests</c>, <c>SessionCqrsTests</c>, <c>CollaborationLockSweepJobTests</c> and
    /// <c>CollaborationRegistrationTests</c>, by name: each handler does what its service does, each request carries its key
    /// (view for an editor's act, manage for closing a session and taking a lock over), the sweep expires only lapsed leases,
    /// and the module composes. <b>CHANGED:</b> the sweep job's switch is <c>Collaboration:Enabled</c> (the predecessor platform: a tenant
    /// directory) and no request carries a user.
    /// </summary>
    public sealed class CollaborationRequestsTests
    {
        private static readonly ResourceRef Board = ResourceRef.Of("dashboard", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));

        private static string Key<TRequest>() => typeof(TRequest).GetCustomAttributes<RequiresAttribute>().Single().Permission!;

        [Fact]
        public async Task AcquireLockCommand_TakesTheLease()
        {
            var rig = new CollaborationRig();

            var result = await new AcquireLockCommandHandler(rig.LockService).Handle(new AcquireLockCommand("dashboard", Board.ResourceId, LockScope.Resource, 60), CancellationToken.None);

            Assert.Equal(new ResourceLockResult(true, Ada, "Ada", Start.AddSeconds(60)), result);
        }

        [Fact]
        public async Task RenewLockCommand_ExtendsTheLease()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            rig.Clock.Now = Start.AddSeconds(30);

            var result = await new RenewLockCommandHandler(rig.LockService).Handle(new RenewLockCommand("dashboard", Board.ResourceId, LockScope.Resource, 600), CancellationToken.None);

            Assert.Equal(Start.AddSeconds(630), result!.ExpiresUtc);
        }

        [Fact]
        public async Task ReleaseLockCommand_ReleasesTheLease()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            await new ReleaseLockCommandHandler(rig.LockService).Handle(new ReleaseLockCommand("dashboard", Board.ResourceId), CancellationToken.None);

            Assert.True(rig.LockRows.Rows.Single().Released);
        }

        [Fact]
        public void LockCommands_RequireTheViewPermission()
        {
            Assert.Equal(CollaborationPermissions.View, Key<AcquireLockCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<RenewLockCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<ReleaseLockCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<GetLockQuery>());

            // ADDED: taking another user's lock over is the administrator's (D114).
            Assert.Equal(CollaborationPermissions.Manage, Key<ForceReleaseLockCommand>());
            Assert.Equal(CollaborationPermissions.Manage, Key<GetCollaborationOverviewQuery>());
        }

        [Fact]
        public void AddCollaboration_RegistersTheLockServiceAndTheSweepJob()
        {
            var services = Composed();

            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ResourceLockService));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRequestHandler<AcquireLockCommand, ResourceLockResult>));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(CollaborationSweeper));

            // The job is found by the host's assembly scan, like every framework job.
            Assert.True(typeof(HostedServiceBase).IsAssignableFrom(typeof(CollaborationSweepJob)));
        }

        [Fact]
        public async Task JoinEditSessionCommand_OpensOrJoins_AndReturnsTheSessionView()
        {
            var rig = new CollaborationRig();

            var view = await new JoinEditSessionCommandHandler(rig.SessionService).Handle(new JoinEditSessionCommand("dashboard", Board.ResourceId), CancellationToken.None);

            Assert.Equal(("dashboard", Board.ResourceId, "Open"), (view.ResourceType, view.ResourceId, view.Status));
            Assert.Equal((Ada, "Ada"), (Assert.Single(view.Participants).UserId, view.Participants[0].DisplayName));
        }

        [Fact]
        public async Task UpdatePresenceCommand_MovesTheParticipant()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);

            await new UpdatePresenceCommandHandler(rig.SessionService).Handle(new UpdatePresenceCommand(session.Id, "widget:4", "7"), CancellationToken.None);

            Assert.Equal(("widget:4", "7"), (rig.SessionRows.Rows.Single().Participants[0].ActiveRegion, rig.SessionRows.Rows.Single().Participants[0].CursorPosition));
        }

        [Fact]
        public async Task BroadcastChangeCommand_AppendsTheEntry_AndReturnsTheUpdatedView()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);

            var view = await new BroadcastChangeCommandHandler(rig.SessionService).Handle(new BroadcastChangeCommand(session.Id, "layout changed"), CancellationToken.None);

            Assert.Equal((Ada, "layout changed"), (Assert.Single(view.Changes).AuthorUserId, view.Changes[0].Descriptor));
        }

        [Fact]
        public async Task LeaveEditSessionCommand_RemovesTheParticipant()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            await rig.As(Bo, "Bo").SessionService.JoinAsync(Board);

            await new LeaveEditSessionCommandHandler(rig.SessionService).Handle(new LeaveEditSessionCommand(session.Id), CancellationToken.None);

            Assert.Equal(Ada, Assert.Single(rig.SessionRows.Rows.Single().Participants).UserId);
        }

        [Fact]
        public async Task CloseEditSessionCommand_EndsTheSession()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);

            await new CloseEditSessionCommandHandler(rig.As(Cy, "Cy").SessionService).Handle(new CloseEditSessionCommand(session.Id), CancellationToken.None);

            Assert.Equal(EditSessionStatus.Closed, rig.SessionRows.Rows.Single().Status);
        }

        [Fact]
        public async Task GetEditSessionQuery_ReturnsTheView_OrNullWhenAbsent()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            var handler = new GetEditSessionQueryHandler(rig.SessionService);

            Assert.Equal(session.Id, (await handler.Handle(new GetEditSessionQuery(session.Id), CancellationToken.None))!.Id);
            Assert.Null(await handler.Handle(new GetEditSessionQuery(Guid.NewGuid()), CancellationToken.None));
        }

        [Fact]
        public async Task GetOpenEditSessionQuery_ReturnsTheOpenSessionForTheResource()
        {
            var rig = new CollaborationRig();
            var session = await rig.SessionService.JoinAsync(Board);
            var handler = new GetOpenEditSessionQueryHandler(rig.As(Bo, "Bo").SessionService);

            Assert.Equal(session.Id, (await handler.Handle(new GetOpenEditSessionQuery("dashboard", Board.ResourceId), CancellationToken.None))!.Id);
            Assert.Null(await handler.Handle(new GetOpenEditSessionQuery("pipeline", "def-9"), CancellationToken.None));

            // ADDED: a resource the caller may not edit shows no session at all.
            rig.Access.Denied.Add(Board.Value);
            Assert.Null(await handler.Handle(new GetOpenEditSessionQuery("dashboard", Board.ResourceId), CancellationToken.None));
        }

        [Fact]
        public void SessionRequests_RequireTheViewPermission()
        {
            Assert.Equal(CollaborationPermissions.View, Key<JoinEditSessionCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<UpdatePresenceCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<BroadcastChangeCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<LeaveEditSessionCommand>());
            Assert.Equal(CollaborationPermissions.View, Key<GetEditSessionQuery>());
            Assert.Equal(CollaborationPermissions.View, Key<GetOpenEditSessionQuery>());
        }

        [Fact]
        public void CloseEditSessionCommand_RequiresTheManagePermission() =>
            Assert.Equal(CollaborationPermissions.Manage, Key<CloseEditSessionCommand>());

        [Fact]
        public void AddCollaboration_RegistersTheEditSessionService()
        {
            var services = Composed();

            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(EditSessionService));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IRequestHandler<JoinEditSessionCommand, EditSessionView>));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Stomme.Erasure.ISubjectErasureParticipant) && descriptor.ImplementationType == typeof(CollaborationErasure));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Stomme.Portability.ISubjectDataContributor) && descriptor.ImplementationType == typeof(CollaborationSubjectContributor));
        }

        [Fact]
        public async Task ExecuteAsync_ExpiresOnlyLapsedLeases()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource, TimeSpan.FromMinutes(1));
            await rig.As(Bo, "Bo").LockService.AcquireAsync(ResourceRef.Of("pipeline", "def-1"), LockScope.Resource, TimeSpan.FromMinutes(10));
            rig.Clock.Now = Start.AddMinutes(2);

            var expired = await new CollaborationSweeper(rig.Locks, rig.Clock, rig.UnitOfWork).SweepSiteAsync(Site);

            Assert.Equal(1, expired);
            Assert.True(rig.LockRows.Rows.Single(row => row.HolderUserId == Ada).Released);
            Assert.False(rig.LockRows.Rows.Single(row => row.HolderUserId == Bo).Released);
        }

        [Fact]
        public async Task ExecuteAsync_WithNothingLapsed_SweepsNothing()
        {
            var rig = new CollaborationRig();
            await rig.LockService.AcquireAsync(Board, LockScope.Resource);
            var writes = rig.LockRows.Writes;

            Assert.Equal(0, await new CollaborationSweeper(rig.Locks, rig.Clock, rig.UnitOfWork).SweepSiteAsync(Site));
            Assert.Equal(writes, rig.LockRows.Writes);
        }

        [Fact]
        public async Task ExecuteAsync_WithoutATenantDirectory_DoesNothing()
        {
            // CHANGED: the job's own switch is Collaboration:Enabled - off, it touches no site at all.
            var services = new ServiceCollection();
            services.AddSingleton(new CollaborationOptions { Enabled = false });
            await using var provider = services.BuildServiceProvider();

            var log = await new CollaborationSweepJob(provider.GetRequiredService<IServiceScopeFactory>()).ExecuteJobAsync(provider);

            Assert.Contains("Switched Off", log);
        }

        [Fact]
        public void AddCollaboration_BuildsAndResolvesTheAggregateRepositories()
        {
            var services = Composed();

            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Stomme.Collaboration.Persistence.IResourceLockRepository));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Stomme.Collaboration.Persistence.IEditSessionRepository));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Stomme.SharedKernel.Domain.IRepository<ResourceLockAggregate, Guid>));
            Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(Stomme.SharedKernel.Domain.IRepository<EditSessionAggregate, Guid>));
        }

        [Fact]
        public void Module_ContributesTheCollaborationPermissions()
        {
            var declared = PermissionKeys.ParseKeys(new Stomme.Modules.Collaboration.ModuleInfo().ModuleDefinition.PermissionNames).Select(key => key.Key).ToList();

            Assert.Contains(CollaborationPermissions.View, declared);
            Assert.Contains(CollaborationPermissions.Manage, declared);
        }

        private static IServiceCollection Composed()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server",
                    ["ConnectionStrings:ModuleAggregates"] = "Data Source=:memory:",
                })
                .Build());
            new Stomme.Modules.Collaboration.Startup.ServerStartup().ConfigureServices(services);
            return services;
        }
    }
}
