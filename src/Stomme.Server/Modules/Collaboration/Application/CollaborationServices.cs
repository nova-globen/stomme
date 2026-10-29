#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Collaboration.Persistence;
using Stomme.Infrastructure;
using Stomme.Mediator;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Repository;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Collaboration.Application
{
    /// <summary>
    /// <c>Collaboration</c> - the installation switch (off by default; with it off, editing is exactly the
    /// single-editor behaviour) and the lease a lock lives on. Read from THIS host's configuration, the override file
    /// included.
    /// </summary>
    public sealed class CollaborationOptions
    {
        public const string SectionName = "Collaboration";

        public bool Enabled { get; set; }

        /// <summary>A lock's lease when the editor asks for none; it renews while it stays open. Default two minutes.</summary>
        public int LeaseSeconds { get; set; } = 120;

        /// <summary>
        /// How long a participant not heard from stays present: the editor's heartbeat runs every half
        /// minute, and the sweep forgets a participant silent this long - a crashed browser leaves no ghost. Default two minutes.
        /// </summary>
        public int PresenceSeconds { get; set; } = 120;
    }

    /// <summary>A collaboration request refused before anything changed; the message is written to be shown.</summary>
    public sealed class CollaborationRefusedException(CollaborationRefusal reason, string message) : InvalidOperationException(message)
    {
        public CollaborationRefusal Reason { get; } = reason;
    }

    public enum CollaborationRefusal
    {
        /// <summary>No signed-in user on a site.</summary>
        NoCaller = 0,

        /// <summary>The resource does not exist for the user, or its module does not let them edit it.</summary>
        NotAvailable = 1,

        /// <summary>The user is not a participant of the session.</summary>
        NotParticipant = 2,

        /// <summary>A change was broadcast by a user holding no live lock on the resource.</summary>
        NoLiveLock = 3,
    }

    /// <summary>WHO and WHERE for a collaboration call - the request's principal on its site, never an id a caller passes.</summary>
    public interface ICollaborationCaller
    {
        /// <exception cref="CollaborationRefusedException">No signed-in user on a site.</exception>
        Task<CollaborationCallerIdentity> RequireAsync(CancellationToken cancellationToken = default);
    }

    public sealed record CollaborationCallerIdentity(TenantId TenantId, int SiteId, int UserId, string DisplayName);

    public sealed class CollaborationCaller(ICurrentUser currentUser, ITenantContext tenantContext, ITenantManager tenantManager, IUserRepository users) : ICollaborationCaller
    {
        public async Task<CollaborationCallerIdentity> RequireAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var siteId = currentUser.SiteId > 0 ? currentUser.SiteId : (await tenantManager.GetAliasAsync())?.SiteId ?? -1;
            if (currentUser.UserId is not { } userId || userId <= 0 || siteId <= 0 || tenantContext.CurrentTenantId is not { } tenantId)
            {
                throw new CollaborationRefusedException(CollaborationRefusal.NoCaller, "Collaboration is available to a signed-in user on a site.");
            }

            var user = await users.GetUserAsync(userId);
            var name = string.IsNullOrWhiteSpace(user?.DisplayName) ? currentUser.UserName ?? ("user " + userId) : user!.DisplayName;
            return new CollaborationCallerIdentity(tenantId, siteId, userId, name);
        }
    }

    /// <summary>
    /// May the CURRENT user edit this resource? (collaboration never grants authority over a resource.) The owning
    /// module answers: the user must hold its WRITE key - asked of the access guard against the module's own save
    /// request, nothing sent - and the resource must exist for them through its gated read. A type no module owns, or a
    /// module this installation does not compose, answers no.
    /// </summary>
    public interface ICollaborativeResourceAccess
    {
        Task<bool> CanEditAsync(ResourceRef resource, CancellationToken cancellationToken = default);
    }

    public sealed class ModuleCollaborativeResourceAccess(IMediator mediator, IAccessGuard guard, IServiceProvider services) : ICollaborativeResourceAccess
    {
        public async Task<bool> CanEditAsync(ResourceRef resource, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resource);
            try
            {
                switch (resource.ResourceType)
                {
                    case CollaborativeResourceTypes.Pipeline:
                        return Composed<global::Stomme.Modules.Pipelines.Application.GetPipelineQuery, global::Stomme.Models.Pipelines.PipelineDetail?>()
                            && await guard.IsAllowedAsync(typeof(global::Stomme.Modules.Pipelines.Application.SaveGraphCommand), cancellationToken)
                            && await mediator.Send(new global::Stomme.Modules.Pipelines.Application.GetPipelineQuery(resource.ResourceId), cancellationToken) is not null;
                    case CollaborativeResourceTypes.Dashboard:
                        return Guid.TryParse(resource.ResourceId, out var dashboardId)
                            && Composed<global::Stomme.Modules.Dashboards.Application.GetDashboardQuery, global::Stomme.Dashboards.DashboardView?>()
                            && await guard.IsAllowedAsync(typeof(global::Stomme.Modules.Dashboards.Application.AddWidgetCommand), cancellationToken)
                            && await mediator.Send(new global::Stomme.Modules.Dashboards.Application.GetDashboardQuery(dashboardId), cancellationToken) is not null;
                    default:
                        return false;
                }
            }
            catch (AccessDeniedException)
            {
                return false;
            }
        }

        // A module switched off registers no handlers; asking its mediator would be a fault, not an answer.
        private bool Composed<TRequest, TResponse>()
            where TRequest : IRequest<TResponse> =>
            services.GetService<IRequestHandler<TRequest, TResponse>>() is not null;
    }

    /// <summary>
    /// Serialises the check-then-write of a lock or a session open per resource, IN THIS PROCESS - 64 striped gates, so the
    /// memory is bounded and an unlucky pair of resources at worst waits for each other. It is not a distributed lock:
    /// Stomme's <c>IDistributedLock</c> implementations are not composed into the host.
    /// Across nodes of a scaled-out installation two editors can therefore both be granted in the same instant - acceptable
    /// for an ADVISORY lock, whose loser still meets the owning module's optimistic concurrency on save.
    /// </summary>
    public sealed class CollaborationGate
    {
        private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

        public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
        {
            var stripe = _stripes[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_stripes.Length)];
            await stripe.WaitAsync(cancellationToken);
            return new Exit(stripe);
        }

        private sealed class Exit(SemaphoreSlim stripe) : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    stripe.Release();
                }
            }
        }
    }

    /// <summary>
    /// The advisory locks: one live holder per resource and scope. Taking a lock
    /// held by someone else REPORTS them - it never throws; taking your own renews it; lapsed leases met on the way in are
    /// expired first (their release still reaches the other editors). The holder is the request's user
    /// (a holder id in the command would let anyone act as anyone); the resource must be one the user may edit;
    /// an administrator may release another user's live lock.
    /// </summary>
    public sealed class ResourceLockService(
        IResourceLockRepository locks, ICollaborationCaller caller, ICollaborativeResourceAccess access, CollaborationOptions options, CollaborationGate gate, TimeProvider time, IUnitOfWork unitOfWork)
    {
        public async Task<ResourceLockResult> AcquireAsync(ResourceRef resource, LockScope scope, TimeSpan? lease = null, CancellationToken cancellationToken = default)
        {
            var me = await RequireEditorAsync(resource, cancellationToken);
            using (await gate.EnterAsync(Key(me, resource, scope), cancellationToken))
            {
                var now = Now();
                var unreleased = await locks.ListUnreleasedAsync(me.SiteId, resource, scope, cancellationToken);
                await ExpireLapsedAsync(unreleased, now, cancellationToken);
                var live = unreleased.FirstOrDefault(candidate => candidate.IsHeld(now));
                if (live is not null && live.HolderUserId != me.UserId)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken); // the lapsed leases expired on the way in
                    return Result(false, live);
                }

                if (live is not null)
                {
                    // Taking your own lock again is a renewal - idempotent for a reconnecting editor.
                    return await RenewAsync(live, me, now, lease, cancellationToken);
                }

                var acquired = ResourceLockAggregate.Acquire(Guid.NewGuid(), me.TenantId, me.SiteId, resource, scope, me.UserId, me.DisplayName, Lease(lease), now);
                await locks.AddAsync(acquired, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result(true, acquired);
            }
        }

        /// <summary>Extends the caller's own live lease from now; anyone else's, or a lapsed one, is reported, not changed.</summary>
        public async Task<ResourceLockResult?> RenewAsync(ResourceRef resource, LockScope scope, TimeSpan? lease = null, CancellationToken cancellationToken = default)
        {
            var me = await caller.RequireAsync(cancellationToken);
            var now = Now();
            var current = (await locks.ListUnreleasedAsync(me.SiteId, resource, scope, cancellationToken)).FirstOrDefault();
            if (current is null)
            {
                return null;
            }

            return !current.IsHeld(now) || current.HolderUserId != me.UserId
                ? Result(false, current)
                : await RenewAsync(current, me, now, lease, cancellationToken);
        }

        /// <summary>Releases the caller's own lock; a lapsed one is recorded as expired; none at all is a no-op.</summary>
        public async Task ReleaseAsync(ResourceRef resource, LockScope scope, CancellationToken cancellationToken = default)
        {
            var me = await caller.RequireAsync(cancellationToken);
            var current = (await locks.ListUnreleasedAsync(me.SiteId, resource, scope, cancellationToken)).FirstOrDefault();
            if (current is null)
            {
                return;
            }

            if (current.IsExpired(Now()))
            {
                current.Expire(Now());
            }
            else
            {
                // Holder-only: the aggregate refuses anyone else.
                current.Release(me.UserId);
            }

            await locks.UpdateAsync(current, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        /// <summary>An administrator takes a lock over: whoever holds it, it is released and the release recorded.</summary>
        public async Task<bool> ReleaseByAdministratorAsync(ResourceRef resource, LockScope scope, CancellationToken cancellationToken = default)
        {
            var me = await caller.RequireAsync(cancellationToken);
            var current = (await locks.ListUnreleasedAsync(me.SiteId, resource, scope, cancellationToken)).FirstOrDefault(candidate => candidate.IsHeld(Now()));
            if (current is null)
            {
                return false;
            }

            current.ReleaseByAdministrator(me.UserId);
            await locks.UpdateAsync(current, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return true;
        }

        /// <summary>Who holds the resource at the scope now, if anyone - the editor's banner.</summary>
        public async Task<ResourceLockResult?> CurrentAsync(ResourceRef resource, LockScope scope, CancellationToken cancellationToken = default)
        {
            var me = await RequireEditorAsync(resource, cancellationToken);
            var now = Now();
            var live = (await locks.ListUnreleasedAsync(me.SiteId, resource, scope, cancellationToken)).FirstOrDefault(candidate => candidate.IsHeld(now));
            return live is null ? null : Result(live.HolderUserId == me.UserId, live);
        }

        private async Task<CollaborationCallerIdentity> RequireEditorAsync(ResourceRef resource, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(resource);
            var me = await caller.RequireAsync(cancellationToken);
            return await access.CanEditAsync(resource, cancellationToken)
                ? me
                : throw new CollaborationRefusedException(CollaborationRefusal.NotAvailable, $"'{resource}' is not available to edit.");
        }

        private async Task ExpireLapsedAsync(IEnumerable<ResourceLockAggregate> unreleased, DateTime now, CancellationToken cancellationToken)
        {
            foreach (var stale in unreleased.Where(candidate => candidate.IsExpired(now)).ToList())
            {
                stale.Expire(now);
                await locks.UpdateAsync(stale, cancellationToken);
            }
        }

        private async Task<ResourceLockResult> RenewAsync(ResourceLockAggregate current, CollaborationCallerIdentity me, DateTime now, TimeSpan? lease, CancellationToken cancellationToken)
        {
            current.Renew(me.UserId, now, lease is null ? null : Lease(lease));
            await locks.UpdateAsync(current, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result(true, current);
        }

        private TimeSpan Lease(TimeSpan? requested) => requested ?? TimeSpan.FromSeconds(Math.Max(1, options.LeaseSeconds));

        private DateTime Now() => time.GetUtcNow().UtcDateTime;

        private static ResourceLockResult Result(bool acquired, ResourceLockAggregate resourceLock) =>
            new(acquired, resourceLock.HolderUserId, resourceLock.HolderDisplayName, resourceLock.ExpiresUtc);

        internal static string Key(CollaborationCallerIdentity me, ResourceRef resource, LockScope scope) =>
            $"lock:{me.TenantId.Value:D}:{me.SiteId}:{resource.Value}:{scope}";
    }

    /// <summary>
    /// The editing sessions: the first editor of a resource opens its session, the
    /// next joins it; a change is broadcast only by a participant holding a live lock on the resource; the last one out
    /// closes it and gives up their locks. The user is the request's principal, joining needs the right to
    /// EDIT the resource (its module decides), and a session is addressed within the request's site - another site's
    /// answers as absent. The presence push goes over the hub.
    /// </summary>
    public sealed class EditSessionService(
        IEditSessionRepository sessions, IResourceLockRepository locks, ICollaborationCaller caller, ICollaborativeResourceAccess access, CollaborationGate gate, TimeProvider time, IUnitOfWork unitOfWork)
    {
        public async Task<EditSessionAggregate> JoinAsync(ResourceRef resource, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resource);
            var me = await caller.RequireAsync(cancellationToken);
            if (!await access.CanEditAsync(resource, cancellationToken))
            {
                throw new CollaborationRefusedException(CollaborationRefusal.NotAvailable, $"'{resource}' is not available to edit.");
            }

            using (await gate.EnterAsync($"session:{me.TenantId.Value:D}:{me.SiteId}:{resource.Value}", cancellationToken))
            {
                // Under the gate a second opener finds the first one's session and joins it.
                if (await sessions.FindOpenAsync(me.SiteId, resource, cancellationToken) is { } open)
                {
                    return await JoinExistingAsync(open, me, cancellationToken);
                }

                var session = EditSessionAggregate.Open(Guid.NewGuid(), me.TenantId, me.SiteId, resource, Now());
                session.Join(me.UserId, me.DisplayName, Now());
                await sessions.AddAsync(session, cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return session;
            }
        }

        public async Task<EditSessionAggregate> UpdatePresenceAsync(Guid sessionId, string? activeRegion, string? cursorPosition, CancellationToken cancellationToken = default)
        {
            var (session, me) = await ParticipantOfAsync(sessionId, cancellationToken);
            session.UpdatePresence(me.UserId, activeRegion, cursorPosition, Now());
            await sessions.UpdateAsync(session, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return session;
        }

        /// <summary>The editor's heartbeat: the caller is still here. Tells nobody - presence that stays is no news.</summary>
        public async Task HeartbeatAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var (session, me) = await ParticipantOfAsync(sessionId, cancellationToken);
            session.Touch(me.UserId, Now());
            await sessions.UpdateAsync(session, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        public async Task<EditSessionAggregate> BroadcastChangeAsync(Guid sessionId, string descriptor, CancellationToken cancellationToken = default)
        {
            var (session, me) = await ParticipantOfAsync(sessionId, cancellationToken);
            var now = Now();
            if (!(await locks.ListUnreleasedAsync(me.SiteId, session.Resource, cancellationToken)).Any(candidate => candidate.HolderUserId == me.UserId && candidate.IsHeld(now)))
            {
                throw new CollaborationRefusedException(CollaborationRefusal.NoLiveLock, $"You hold no live lock on '{session.Resource}'; take the resource's lock or a field lock before broadcasting a change.");
            }

            session.BroadcastChange(me.UserId, descriptor, now);
            await sessions.UpdateAsync(session, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return session;
        }

        /// <summary>The caller leaves; the last one out closes the session and gives up their locks on the resource.</summary>
        public async Task LeaveAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var (session, me) = await ParticipantOfAsync(sessionId, cancellationToken);
            session.Leave(me.UserId);
            if (session.Participants.Count == 0)
            {
                session.Close();
                await ReleaseLocksAsync(me.SiteId, session.Resource, new[] { me.UserId }, cancellationToken);
            }

            await sessions.UpdateAsync(session, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        /// <summary>An administrator ends a session: every presence released, and every participant's locks on the resource.</summary>
        public async Task CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var me = await caller.RequireAsync(cancellationToken);
            var session = await sessions.GetForSiteAsync(sessionId, me.SiteId, cancellationToken)
                ?? throw new CollaborationRefusedException(CollaborationRefusal.NotAvailable, $"Edit session '{sessionId}' does not exist.");
            var holders = session.Participants.Select(participant => participant.UserId).ToList();
            session.Close();
            await ReleaseLocksAsync(me.SiteId, session.Resource, holders, cancellationToken);
            await sessions.UpdateAsync(session, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        /// <summary>A session of the caller's site that they take part in; null otherwise.</summary>
        public async Task<EditSessionAggregate?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            var me = await caller.RequireAsync(cancellationToken);
            var session = await sessions.GetForSiteAsync(sessionId, me.SiteId, cancellationToken);
            return session is not null && session.Participants.Any(participant => participant.UserId == me.UserId) ? session : null;
        }

        /// <summary>The open session on a resource the caller may edit - who else is there before they join.</summary>
        public async Task<EditSessionAggregate?> FindOpenAsync(ResourceRef resource, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(resource);
            var me = await caller.RequireAsync(cancellationToken);
            return await access.CanEditAsync(resource, cancellationToken) ? await sessions.FindOpenAsync(me.SiteId, resource, cancellationToken) : null;
        }

        private async Task<EditSessionAggregate> JoinExistingAsync(EditSessionAggregate session, CollaborationCallerIdentity me, CancellationToken cancellationToken)
        {
            if (session.Participants.Any(participant => participant.UserId == me.UserId))
            {
                // Already present - a reconnecting editor rejoins idempotently, and is seen.
                session.Touch(me.UserId, Now());
            }
            else
            {
                session.Join(me.UserId, me.DisplayName, Now());
            }

            await sessions.UpdateAsync(session, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return session;
        }

        private async Task<(EditSessionAggregate Session, CollaborationCallerIdentity Me)> ParticipantOfAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            var me = await caller.RequireAsync(cancellationToken);
            var session = await sessions.GetForSiteAsync(sessionId, me.SiteId, cancellationToken);
            return session is not null && session.Participants.Any(participant => participant.UserId == me.UserId)
                ? (session, me)
                : throw new CollaborationRefusedException(CollaborationRefusal.NotParticipant, $"You are not a participant of edit session '{sessionId}'.");
        }

        private async Task ReleaseLocksAsync(int siteId, ResourceRef resource, IReadOnlyCollection<int> holders, CancellationToken cancellationToken)
        {
            var now = Now();
            foreach (var held in (await locks.ListUnreleasedAsync(siteId, resource, cancellationToken)).Where(candidate => candidate.IsHeld(now) && holders.Contains(candidate.HolderUserId)).ToList())
            {
                held.Release(held.HolderUserId);
                await locks.UpdateAsync(held, cancellationToken);
            }
        }

        private DateTime Now() => time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// The sweep: expires the site's lapsed leases, so their release reaches the
    /// other editors even when nobody takes the resource again. Lazy expiry already reclaims a lease the moment someone
    /// does; this is the quiet case's backstop.
    /// </summary>
    /// <summary>
    /// The presence sweep: every open session of the site forgets the participants not heard from within
    /// <see cref="CollaborationOptions.PresenceSeconds"/> - a browser that crashed or lost its network never says it left - and
    /// a session so emptied closes. Their locks are not touched here: a lock lives on its own lease.
    /// </summary>
    public sealed class PresenceSweeper(IEditSessionRepository sessions, CollaborationOptions options, TimeProvider time, IUnitOfWork unitOfWork)
    {
        public async Task<int> SweepSiteAsync(int siteId, CancellationToken cancellationToken = default)
        {
            var cutoff = time.GetUtcNow().UtcDateTime.AddSeconds(-Math.Max(30, options.PresenceSeconds));
            var forgotten = 0;
            foreach (var session in (await sessions.ListForSiteAsync(siteId, cancellationToken)).Where(candidate => candidate.Status == EditSessionStatus.Open).ToList())
            {
                var absent = session.ForgetAbsent(cutoff);
                if (absent.Count > 0)
                {
                    await sessions.UpdateAsync(session, cancellationToken);
                    forgotten += absent.Count;
                }
            }

            if (forgotten > 0)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return forgotten;
        }
    }

    public sealed class CollaborationSweeper(IResourceLockRepository locks, TimeProvider time, IUnitOfWork unitOfWork)
    {
        public async Task<int> SweepSiteAsync(int siteId, CancellationToken cancellationToken = default)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var lapsed = (await locks.ListUnreleasedAsync(siteId, cancellationToken)).Where(candidate => candidate.IsExpired(now)).ToList();
            foreach (var resourceLock in lapsed)
            {
                resourceLock.Expire(now);
                await locks.UpdateAsync(resourceLock, cancellationToken);
            }

            if (lapsed.Count > 0)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return lapsed.Count;
        }
    }
}
