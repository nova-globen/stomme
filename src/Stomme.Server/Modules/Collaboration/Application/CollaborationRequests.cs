#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Mediator;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Security.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Collaboration.Application
{
    // ============================================================================================================
    // The collaboration requests (BL-97 increment 11; the predecessor platform's LockCommands, SessionCommands and SessionQueries), each
    // decided by the host's authorization behaviour before its handler runs (D99). CHANGED from the predecessor platform: no request names
    // a user - the holder, the participant and the author are the request's principal - and a resource is addressed by
    // its type and id, each checked against the owning module (ICollaborativeResourceAccess).
    // ============================================================================================================

    [Requires(CollaborationPermissions.View)]
    public sealed record AcquireLockCommand(string ResourceType, string ResourceId, LockScope Scope = LockScope.Resource, int? LeaseSeconds = null) : ICommand<ResourceLockResult>;

    public sealed class AcquireLockCommandHandler(ResourceLockService locks) : ICommandHandler<AcquireLockCommand, ResourceLockResult>
    {
        public Task<ResourceLockResult> Handle(AcquireLockCommand request, CancellationToken ct) =>
            locks.AcquireAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), request.Scope, CollaborationViews.Lease(request.LeaseSeconds), ct);
    }

    /// <summary>The heartbeat of a live editor; null when there is no lock to renew.</summary>
    [Requires(CollaborationPermissions.View)]
    public sealed record RenewLockCommand(string ResourceType, string ResourceId, LockScope Scope = LockScope.Resource, int? LeaseSeconds = null) : ICommand<ResourceLockResult?>;

    public sealed class RenewLockCommandHandler(ResourceLockService locks) : ICommandHandler<RenewLockCommand, ResourceLockResult?>
    {
        public Task<ResourceLockResult?> Handle(RenewLockCommand request, CancellationToken ct) =>
            locks.RenewAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), request.Scope, CollaborationViews.Lease(request.LeaseSeconds), ct);
    }

    [Requires(CollaborationPermissions.View)]
    public sealed record ReleaseLockCommand(string ResourceType, string ResourceId, LockScope Scope = LockScope.Resource) : ICommand<Unit>;

    public sealed class ReleaseLockCommandHandler(ResourceLockService locks) : ICommandHandler<ReleaseLockCommand, Unit>
    {
        public async Task<Unit> Handle(ReleaseLockCommand request, CancellationToken ct)
        {
            await locks.ReleaseAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), request.Scope, ct);
            return Unit.Value;
        }
    }

    /// <summary>An administrator releases whoever holds the resource (D114's take-over); false when nobody did.</summary>
    [Requires(CollaborationPermissions.Manage)]
    public sealed record ForceReleaseLockCommand(string ResourceType, string ResourceId, LockScope Scope = LockScope.Resource) : ICommand<bool>;

    public sealed class ForceReleaseLockCommandHandler(ResourceLockService locks) : ICommandHandler<ForceReleaseLockCommand, bool>
    {
        public Task<bool> Handle(ForceReleaseLockCommand request, CancellationToken ct) =>
            locks.ReleaseByAdministratorAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), request.Scope, ct);
    }

    /// <summary>Who holds the resource now, if anyone.</summary>
    [Requires(CollaborationPermissions.View)]
    public sealed record GetLockQuery(string ResourceType, string ResourceId, LockScope Scope = LockScope.Resource) : IQuery<ResourceLockResult?>;

    public sealed class GetLockQueryHandler(ResourceLockService locks) : IQueryHandler<GetLockQuery, ResourceLockResult?>
    {
        public Task<ResourceLockResult?> Handle(GetLockQuery request, CancellationToken ct) =>
            locks.CurrentAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), request.Scope, ct);
    }

    [Requires(CollaborationPermissions.View)]
    public sealed record JoinEditSessionCommand(string ResourceType, string ResourceId) : ICommand<EditSessionView>;

    public sealed class JoinEditSessionCommandHandler(EditSessionService sessions) : ICommandHandler<JoinEditSessionCommand, EditSessionView>
    {
        public async Task<EditSessionView> Handle(JoinEditSessionCommand request, CancellationToken ct) =>
            CollaborationViews.Map(await sessions.JoinAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), ct));
    }

    [Requires(CollaborationPermissions.View)]
    public sealed record UpdatePresenceCommand(Guid SessionId, string? ActiveRegion, string? CursorPosition) : ICommand<Unit>;

    public sealed class UpdatePresenceCommandHandler(EditSessionService sessions) : ICommandHandler<UpdatePresenceCommand, Unit>
    {
        public async Task<Unit> Handle(UpdatePresenceCommand request, CancellationToken ct)
        {
            await sessions.UpdatePresenceAsync(request.SessionId, request.ActiveRegion, request.CursorPosition, ct);
            return Unit.Value;
        }
    }

    /// <summary>The editor's heartbeat (increment 12): still here.</summary>
    [Requires(CollaborationPermissions.View)]
    public sealed record HeartbeatEditSessionCommand(Guid SessionId) : ICommand<Unit>;

    public sealed class HeartbeatEditSessionCommandHandler(EditSessionService sessions) : ICommandHandler<HeartbeatEditSessionCommand, Unit>
    {
        public async Task<Unit> Handle(HeartbeatEditSessionCommand request, CancellationToken ct)
        {
            await sessions.HeartbeatAsync(request.SessionId, ct);
            return Unit.Value;
        }
    }

    [Requires(CollaborationPermissions.View)]
    public sealed record BroadcastChangeCommand(Guid SessionId, string Descriptor) : ICommand<EditSessionView>;

    public sealed class BroadcastChangeCommandHandler(EditSessionService sessions) : ICommandHandler<BroadcastChangeCommand, EditSessionView>
    {
        public async Task<EditSessionView> Handle(BroadcastChangeCommand request, CancellationToken ct) =>
            CollaborationViews.Map(await sessions.BroadcastChangeAsync(request.SessionId, request.Descriptor, ct));
    }

    [Requires(CollaborationPermissions.View)]
    public sealed record LeaveEditSessionCommand(Guid SessionId) : ICommand<Unit>;

    public sealed class LeaveEditSessionCommandHandler(EditSessionService sessions) : ICommandHandler<LeaveEditSessionCommand, Unit>
    {
        public async Task<Unit> Handle(LeaveEditSessionCommand request, CancellationToken ct)
        {
            await sessions.LeaveAsync(request.SessionId, ct);
            return Unit.Value;
        }
    }

    [Requires(CollaborationPermissions.Manage)]
    public sealed record CloseEditSessionCommand(Guid SessionId) : ICommand<Unit>;

    public sealed class CloseEditSessionCommandHandler(EditSessionService sessions) : ICommandHandler<CloseEditSessionCommand, Unit>
    {
        public async Task<Unit> Handle(CloseEditSessionCommand request, CancellationToken ct)
        {
            await sessions.CloseAsync(request.SessionId, ct);
            return Unit.Value;
        }
    }

    /// <summary>A session of this site the caller takes part in, or null.</summary>
    [Requires(CollaborationPermissions.View)]
    public sealed record GetEditSessionQuery(Guid SessionId) : IQuery<EditSessionView?>;

    public sealed class GetEditSessionQueryHandler(EditSessionService sessions) : IQueryHandler<GetEditSessionQuery, EditSessionView?>
    {
        public async Task<EditSessionView?> Handle(GetEditSessionQuery request, CancellationToken ct) =>
            await sessions.GetAsync(request.SessionId, ct) is { } session ? CollaborationViews.Map(session) : null;
    }

    /// <summary>The open session on a resource the caller may edit, or null.</summary>
    [Requires(CollaborationPermissions.View)]
    public sealed record GetOpenEditSessionQuery(string ResourceType, string ResourceId) : IQuery<EditSessionView?>;

    public sealed class GetOpenEditSessionQueryHandler(EditSessionService sessions) : IQueryHandler<GetOpenEditSessionQuery, EditSessionView?>
    {
        public async Task<EditSessionView?> Handle(GetOpenEditSessionQuery request, CancellationToken ct) =>
            await sessions.FindOpenAsync(ResourceRef.Of(request.ResourceType, request.ResourceId), ct) is { } session ? CollaborationViews.Map(session) : null;
    }

    public sealed class AcquireLockCommandValidator : AbstractValidator<AcquireLockCommand>
    {
        public AcquireLockCommandValidator()
        {
            RuleFor(command => command.ResourceType).NotEmpty().MaximumLength(50);
            RuleFor(command => command.ResourceId).NotEmpty().MaximumLength(ResourceRef.MaxIdLength);
            RuleFor(command => command.LeaseSeconds).InclusiveBetween(1, (int)ResourceLockAggregate.MaxLease.TotalSeconds).When(command => command.LeaseSeconds is not null);
        }
    }

    public sealed class RenewLockCommandValidator : AbstractValidator<RenewLockCommand>
    {
        public RenewLockCommandValidator()
        {
            RuleFor(command => command.ResourceType).NotEmpty().MaximumLength(50);
            RuleFor(command => command.ResourceId).NotEmpty().MaximumLength(ResourceRef.MaxIdLength);
            RuleFor(command => command.LeaseSeconds).InclusiveBetween(1, (int)ResourceLockAggregate.MaxLease.TotalSeconds).When(command => command.LeaseSeconds is not null);
        }
    }

    public sealed class BroadcastChangeCommandValidator : AbstractValidator<BroadcastChangeCommand>
    {
        public BroadcastChangeCommandValidator()
        {
            RuleFor(command => command.Descriptor).NotEmpty().MaximumLength(EditSessionAggregate.MaxDescriptorLength);
        }
    }

    /// <summary>The site's live locks and open sessions (D114's take-over needs to see them) - the administrator's overview.</summary>
    [Requires(CollaborationPermissions.Manage)]
    public sealed record GetCollaborationOverviewQuery : IQuery<CollaborationOverviewView>;

    public sealed class GetCollaborationOverviewQueryHandler(Stomme.Collaboration.Persistence.IResourceLockRepository locks, Stomme.Collaboration.Persistence.IEditSessionRepository sessions, ICollaborationCaller caller, TimeProvider time)
        : IQueryHandler<GetCollaborationOverviewQuery, CollaborationOverviewView>
    {
        public async Task<CollaborationOverviewView> Handle(GetCollaborationOverviewQuery request, CancellationToken ct)
        {
            var me = await caller.RequireAsync(ct);
            var now = time.GetUtcNow().UtcDateTime;
            var live = (await locks.ListUnreleasedAsync(me.SiteId, ct))
                .Where(candidate => candidate.IsHeld(now))
                .OrderBy(candidate => candidate.Resource.Value, StringComparer.Ordinal)
                .Select(candidate => new LockView(candidate.Resource.ResourceType, candidate.Resource.ResourceId, candidate.Scope, candidate.HolderUserId, candidate.HolderDisplayName, candidate.AcquiredUtc, candidate.ExpiresUtc))
                .ToList();
            var open = (await sessions.ListForSiteAsync(me.SiteId, ct))
                .Where(session => session.Status == EditSessionStatus.Open)
                .OrderBy(session => session.StartedUtc)
                .Select(CollaborationViews.Map)
                .ToList();
            return new CollaborationOverviewView(live, open);
        }
    }

    public static class CollaborationViews
    {
        public static EditSessionView Map(EditSessionAggregate session) =>
            new(
                session.Id,
                session.Resource.ResourceType,
                session.Resource.ResourceId,
                session.Status.ToString(),
                session.StartedUtc,
                session.Participants.Select(participant => new ParticipantView(participant.UserId, participant.DisplayName, participant.JoinedUtc, participant.ActiveRegion, participant.CursorPosition)).ToList(),
                session.Changes.Select(change => new ChangeEntryView(change.AuthorUserId, change.AtUtc, change.Descriptor)).ToList());

        internal static TimeSpan? Lease(int? seconds) => seconds is { } value ? TimeSpan.FromSeconds(value) : null;
    }
}
