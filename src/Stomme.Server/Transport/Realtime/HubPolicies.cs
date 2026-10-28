using System.Collections.Generic;

namespace Stomme.Transport.Realtime
{
    /// <summary>
    /// Per-hub authorization policy names for the realtime transport (brief 04 / D10). Each platform hub is
    /// mapped with <c>RequireAuthorization(policy)</c>, so the WebSocket connection is authorized with the same
    /// identity/RBAC as the rest of the host. The transport registration adds these as named policies; a module
    /// hub can instead pass a Stomme permission-policy name (the <c>Entity:Permission:Roles</c> form that
    /// <c>AuthorizationPolicyProvider</c> resolves) to gate on a specific permission.
    /// </summary>
    /// <remarks>Adopted from the predecessor platform's <c>HubPolicies</c>.</remarks>
    public static class HubPolicies
    {
        /// <summary>In-app notifications hub — any authenticated user (SignalR's per-user grouping targets the recipient).</summary>
        public const string Notification = "hub:notification";

        /// <summary>Health hub - <c>health:view</c> on the connecting request's site (BL-83 / D104). It carries the health
        /// page's live update, so it is the page's own key; before that push existed it broadcast nothing and was
        /// authenticated-only.</summary>
        public const string Health = "hub:health";

        public const string Dashboards = "hub:dashboards";

        /// <summary>Collaboration hub - <c>collaboration:view</c> (BL-97 increment 12), the key an editor's lock and session need.</summary>
        public const string Collaboration = "hub:collaboration";

        /// <summary>Policy name → required permission (null = authenticated only). The producing features tighten these
        /// as they land: the notification hub is authenticated-only (it targets the recipient user), the health hub
        /// requires <c>health:view</c>.</summary>
        public static IReadOnlyList<(string Name, string Permission)> All { get; } = new[]
        {
            (Notification, (string)null),
            (Health, Stomme.Health.HealthPermissions.View),
            (Dashboards, Stomme.Dashboards.DashboardPermissions.View),
            (Collaboration, Stomme.Collaboration.CollaborationPermissions.View),
        };
    }
}
