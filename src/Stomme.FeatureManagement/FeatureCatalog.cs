using Stomme.FeatureManagement.Application;

namespace Stomme.FeatureManagement;

/// <summary>
/// The feature flags the framework itself declares. Each one gates a real capability (see the call sites
/// noted below) rather than access to it — access stays with the permission/role model. Modules declare
/// their own flags by contributing an <see cref="IFeatureContributor"/>; see
/// <c>src/Stomme.FeatureManagement/README.md</c> for the contribution pattern.
/// </summary>
public static class FeatureCatalog
{
    /// <summary>Gates dispatch of queued notification e-mail in the notification job.</summary>
    public const string EmailNotifications = "notifications.email";

    /// <summary>
    /// Gates delivery of queued notification requests through the channel pipeline (BL-78 / D100). The dispatch
    /// job consults it; the e-mail rows a channel writes are still sent by <see cref="EmailNotifications"/>.
    /// </summary>
    public const string NotificationDispatch = "notifications.dispatch";

    /// <summary>Gates recording and retention of site visitor activity.</summary>
    public const string VisitorTracking = "visitors.tracking";

    /// <summary>
    /// Gates what a SITE publishes over MCP (BL-80 / D101 item 9). It narrows the install-wide
    /// <c>Mcp:Server:Enabled</c> switch; it does not duplicate it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The config switch decides whether the endpoint EXISTS: it is read while the container is composed, so turning
    /// it off needs a RESTART, and a route cannot be un-mapped per request. This flag is the runtime half - what the
    /// catalog serves, decided per call - so publishing can be stopped NOW, without recomposing the host. For a
    /// capability that hands tools to an automated client, having a switch that takes effect immediately is the point.
    /// </para>
    /// <para>
    /// <b>Its scope is the TENANT, not the site</b> (<c>IFeatureAdminService.ToggleAsync</c> writes a flag row for the
    /// current tenant), so on the default install - every site in one tenant - it is an install-wide runtime switch.
    /// A deployment with separate tenant databases can stop one of them publishing and leave the others alone. The
    /// per-SITE question is answered by the permission keys, which is where it belongs.
    /// </para>
    /// <para>
    /// With it off the catalog lists nothing and runs nothing, and a refused call is audited like any other, so a
    /// tenant that was switched off does not simply go quiet. Default ON: an install that turns the endpoint on gets
    /// what it asked for without a second thing to find.
    /// </para>
    /// </remarks>
    public const string McpServer = "mcp.server";

    public static readonly IReadOnlyCollection<FeatureDescriptor> All =
    [
        new(EmailNotifications, "Dispatch queued notification e-mail over SMTP.", Category: "Notifications", DefaultEnabled: true),
        new(NotificationDispatch, "Deliver queued notification requests through the channel pipeline.", Category: "Notifications", DefaultEnabled: true),
        new(VisitorTracking, "Record and retain site visitor activity.", Category: "Visitors", DefaultEnabled: true),
        new(McpServer, "Publish this site's resources and tools to MCP clients.", Category: "MCP", DefaultEnabled: true),
    ];
}

/// <summary>
/// Contributes the framework's own flags to the catalog. Discovered by reflection (public parameterless
/// constructor) the same way <c>IServerStartup</c> is — this type is the reference shape for a module
/// author's contributor.
/// </summary>
public sealed class CoreFeatureContributor : IFeatureContributor
{
    public IReadOnlyCollection<FeatureDescriptor> Features => FeatureCatalog.All;
}
