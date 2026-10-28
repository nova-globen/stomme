#nullable enable
using System;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Mediator;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Controllers;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Collaboration.Services
{
    /// <summary>
    /// The collaboration service on the server (the Guestbook pattern) - the overview's and, since increment 12, an editor's
    /// panel's on an Interactive Server circuit: the same gated requests the API sends, through the mediator, each refusal
    /// turned into the same code and message the API answers with.
    /// </summary>
    public sealed class ServerCollaborationService(IMediator mediator, CollaborationOptions options) : ICollaborationService, ITransientService
    {
        public Task<CollaborationOutcome<CollaborationOverviewView>> OverviewAsync() =>
            RunAsync(async () => await mediator.Send(new GetCollaborationOverviewQuery()));

        public async Task<CollaborationOutcome<bool>> TakeOverAsync(LockRequest request)
        {
            var outcome = await RunAsync<object>(async () => await mediator.Send(new ForceReleaseLockCommand(request.ResourceType, request.ResourceId, request.Scope)) ? new object() : null);
            return new CollaborationOutcome<bool>(outcome.Refusal is null, outcome.Refusal!);
        }

        public async Task<CollaborationOutcome<bool>> CloseSessionAsync(Guid sessionId)
        {
            var outcome = await RunAsync<object>(async () =>
            {
                await mediator.Send(new CloseEditSessionCommand(sessionId));
                return new object();
            });
            return new CollaborationOutcome<bool>(outcome.Refusal is null, outcome.Refusal!);
        }

        public Task<CollaborationOutcome<EditSessionView>> JoinAsync(string resourceType, string resourceId) =>
            RunAsync(async () => await mediator.Send(new JoinEditSessionCommand(resourceType, resourceId)));

        public Task<CollaborationOutcome<EditSessionView>> SessionAsync(Guid sessionId) =>
            RunAsync(async () => await mediator.Send(new GetEditSessionQuery(sessionId)));

        public Task<CollaborationOutcome<bool>> PresenceAsync(Guid sessionId, string activeRegion) =>
            DoneAsync(() => mediator.Send(new UpdatePresenceCommand(sessionId, activeRegion, null)));

        public Task<CollaborationOutcome<EditSessionView>> AnnounceAsync(Guid sessionId, string descriptor) =>
            RunAsync(async () => await mediator.Send(new BroadcastChangeCommand(sessionId, descriptor)));

        public Task<CollaborationOutcome<bool>> LeaveAsync(Guid sessionId) =>
            DoneAsync(() => mediator.Send(new LeaveEditSessionCommand(sessionId)));

        public Task<CollaborationOutcome<bool>> HeartbeatAsync(Guid sessionId) =>
            DoneAsync(() => mediator.Send(new HeartbeatEditSessionCommand(sessionId)));

        public Task<CollaborationOutcome<ResourceLockResult>> AcquireAsync(LockRequest request) =>
            RunAsync(async () => await mediator.Send(new AcquireLockCommand(request.ResourceType, request.ResourceId, request.Scope, request.LeaseSeconds)));

        public Task<CollaborationOutcome<ResourceLockResult>> RenewAsync(LockRequest request) =>
            RunAsync(async () => await mediator.Send(new RenewLockCommand(request.ResourceType, request.ResourceId, request.Scope, request.LeaseSeconds)), emptyIsAnswer: true);

        public Task<CollaborationOutcome<bool>> ReleaseAsync(LockRequest request) =>
            DoneAsync(() => mediator.Send(new ReleaseLockCommand(request.ResourceType, request.ResourceId, request.Scope)));

        public Task<CollaborationOutcome<ResourceLockResult>> LockAsync(string resourceType, string resourceId) =>
            RunAsync(async () => await mediator.Send(new GetLockQuery(resourceType, resourceId)), emptyIsAnswer: true);

        private async Task<CollaborationOutcome<bool>> DoneAsync(Func<Task<Unit>> send)
        {
            var outcome = await RunAsync<object>(async () =>
            {
                await send();
                return new object();
            });
            return new CollaborationOutcome<bool>(outcome.Refusal is null, outcome.Refusal!);
        }

        private async Task<CollaborationOutcome<T>> RunAsync<T>(Func<Task<T?>> send, bool emptyIsAnswer = false)
            where T : class
        {
            if (!options.Enabled)
            {
                return new CollaborationOutcome<T>(null!, new CollaborationRefusalView("Disabled", "Collaboration is switched off for this installation (Collaboration:Enabled)."));
            }

            try
            {
                return await send() is { } value
                    ? new CollaborationOutcome<T>(value, null!)
                    : emptyIsAnswer
                        ? new CollaborationOutcome<T>(null!, null!) // nobody holds it: an answer, like the API's 204
                        : new CollaborationOutcome<T>(null!, new CollaborationRefusalView("404", "That is no longer there."));
            }
            catch (Exception ex) when (CollaborationRefusals.Describe(ex) is { } refusal)
            {
                return new CollaborationOutcome<T>(null!, refusal.View);
            }
            catch (AccessDeniedException)
            {
                return new CollaborationOutcome<T>(null!, new CollaborationRefusalView("403", "You do not have access to that."));
            }
        }
    }
}
