#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Collaboration
{
    // ============================================================================================================
    // The Collaboration module's Shared contracts: the keys, the resource kinds an editor can be
    // coordinated on, and what a page reads. BCL-ONLY like the other Shared contracts.
    // ============================================================================================================

    /// <summary>
    /// <c>collaboration:view</c> - join an editor's session, take and renew an advisory lock, see who else is there;
    /// <c>collaboration:manage</c> - close a session and release another user's lock. Neither grants anything over the
    /// RESOURCE: joining or locking one needs the owning module's own write key for it as well.
    /// </summary>
    public static class CollaborationPermissions
    {
        public const string Resource = "collaboration";

        public const string View = "collaboration:view";

        public const string Manage = "collaboration:manage";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
        };
    }

    /// <summary>The resource kinds an editor coordinates on - each resolved through the module that owns it.</summary>
    public static class CollaborativeResourceTypes
    {
        /// <summary>A pipeline, by its engine definition id.</summary>
        public const string Pipeline = "pipeline";

        /// <summary>A dashboard, by its id.</summary>
        public const string Dashboard = "dashboard";
    }

    /// <summary>The granularity an advisory lock covers.</summary>
    public enum LockScope
    {
        /// <summary>The whole resource (the entire pipeline or dashboard).</summary>
        Resource = 0,

        /// <summary>One element of the resource (a step's form, a widget).</summary>
        Field = 1,
    }

    /// <summary>
    /// The outcome of taking or renewing an advisory lock: whether the caller now holds it, who does (the caller on success,
    /// the other holder otherwise - never an error), and when the lease lapses without renewal.
    /// </summary>
    public sealed record ResourceLockResult(bool Acquired, int HolderUserId, string? HolderDisplayName, DateTime ExpiresUtc);

    public sealed record ParticipantView(int UserId, string DisplayName, DateTime JoinedUtc, string? ActiveRegion, string? CursorPosition);

    public sealed record ChangeEntryView(int AuthorUserId, DateTime AtUtc, string Descriptor);

    /// <summary>An editor's session as its participants see it: who is there, where, and what changed.</summary>
    public sealed record EditSessionView(
        Guid Id,
        string ResourceType,
        string ResourceId,
        string Status,
        DateTime StartedUtc,
        IReadOnlyList<ParticipantView> Participants,
        IReadOnlyList<ChangeEntryView> Changes);

    /// <summary>A lock as the site's administrator sees it on the overview.</summary>
    public sealed record LockView(string ResourceType, string ResourceId, LockScope Scope, int HolderUserId, string HolderDisplayName, DateTime AcquiredUtc, DateTime ExpiresUtc);

    /// <summary>The site's live locks and open sessions - the administrator's overview (an administrator may take a lock over).</summary>
    public sealed record CollaborationOverviewView(IReadOnlyList<LockView> Locks, IReadOnlyList<EditSessionView> Sessions);

    /// <summary>A lock to take, renew, release or take over; the lease in seconds, or the site's default.</summary>
    public sealed record LockRequest(string ResourceType, string ResourceId, LockScope Scope = LockScope.Resource, int? LeaseSeconds = null);

    /// <summary>The resource an editor opens.</summary>
    public sealed record JoinSessionRequest(string ResourceType, string ResourceId);

    /// <summary>Where the participant is: the element and a cursor.</summary>
    public sealed record PresenceRequest(string? ActiveRegion, string? CursorPosition);

    /// <summary>A change announced to the other participants - a short descriptor, never the content.</summary>
    public sealed record ChangeRequest(string Descriptor);

    /// <summary>Why a collaboration request was refused, safe to show.</summary>
    public sealed record CollaborationRefusalView(string Code, string Message);
}