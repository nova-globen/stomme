#nullable enable
using System;
using Microsoft.Extensions.Configuration;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// The one place <c>Metering:Enabled</c> is read (D102 item 11).
    /// </summary>
    /// <remarks>
    /// <b>Default OFF, and that is deliberate - but be precise about WHAT it gates.</b> The module registers its
    /// ports and its persistence unconditionally, because a producer must be able to take <c>IUsageRecorder</c>
    /// from DI on any install. This switch gates the two JOBS: with it off, both return immediately and nothing
    /// runs in the background.
    /// <para>
    /// It does NOT gate the module's PAGES. Whether a module's page templates are seeded is the framework's own
    /// <c>module.metering.enabled</c> knob (<c>ModuleFeatureFlags</c>, D85), and an operator who wants the pages
    /// absent sets that. An earlier version of this remark claimed the switch suppressed the pages too; it does
    /// not, and saying so would have made a stock install sound quieter than it is - the pages are seeded, they
    /// are gated by permission, and they show a site with no quotas and no usage.
    /// </para>
    /// </remarks>
    public static class MeteringSwitch
    {
        public const string EnabledKey = "Metering:Enabled";

        /// <summary>Whether metering is enabled for this installation. Absent or unparsable means OFF.</summary>
        public static bool IsEnabled(IConfiguration? configuration) =>
            configuration is not null
            && bool.TryParse(configuration[EnabledKey], out var enabled)
            && enabled;
    }
}
