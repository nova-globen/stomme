namespace Stomme.Transport.Realtime
{
    /// <summary>
    /// Versioned SignalR hub routes (<c>/hubs/v{major}/...</c>). New hub versions can coexist with old ones at
    /// distinct paths, mirroring the gRPC proto package versioning.
    /// </summary>
    /// <remarks>
    /// Lives in <c>Stomme.Shared</c> (BL-78 / D100 point 8) so a Client component - the notification center's live
    /// feed - can connect to a hub by route; the hubs themselves stay server-side in <c>Stomme.Server</c>, which
    /// forwards this type (<c>[assembly: TypeForwardedTo]</c>) for binaries compiled against its old location. The
    /// namespace is deliberately unchanged.
    /// </remarks>
    public static class HubRoutes
    {
        public const string Notification = "/hubs/v1/notification";
        public const string Health = "/hubs/v1/health";

        /// <summary>The Dashboards module's hub (BL-96 increment 10): a site's viewers are told which dataset changed.</summary>
        public const string Dashboards = "/hubs/v1/dashboards";

        /// <summary>The Collaboration module's hub (BL-97 increment 12): a site's editors are told which resource changed and how.</summary>
        public const string Collaboration = "/hubs/v1/collaboration";
    }
}