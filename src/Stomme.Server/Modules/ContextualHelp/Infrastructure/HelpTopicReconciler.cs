#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.FeatureManagement.Application;
using Stomme.Help.Persistence;
using Stomme.Infrastructure;
using Stomme.Modules.ContextualHelp.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.ContextualHelp.Infrastructure
{
    /// <summary>
    /// Brings each site's contributed help topics up to date on the boot/upgrade path, through the
    /// framework's <see cref="SiteContentReconciliationRunner"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is an <see cref="ISiteContentReconciler"/> and nothing else</b>. The runner
    /// enters the site's TENANT scope before it creates the DI scope this reconciler is resolved in, and contains a
    /// throw to this reconciler on this site. A plain loop over the sites would do neither:
    /// one DI scope for every tenant (tenant 2's rows would land under tenant 1) and one catch for the whole loop.
    /// </para>
    /// <para>
    /// <b>What it writes, per contributed topic - and it is the ONLY thing that writes on a boot:</b>
    /// absent row → seeded and published; a row still SEEDED by the SAME contributor whose text CHANGED → updated
    /// (<see cref="HelpTopicAggregate.TryApplySeedUpdate"/>). Everything else is left exactly as it is: an operator's
    /// edit (no longer seeded), a hand-authored topic or draft, an unchanged seed (no write, so its
    /// <c>ContentVersion</c> and its cache key survive the boot), and a row seeded by ANOTHER
    /// contributor (an ownership change is an operator's "reset to seed", never a boot). A key rejected
    /// as a collision is not in the registry at all, so its existing row is untouched and none is created.
    /// </para>
    /// <para>
    /// <b>Idempotent by construction:</b> every topic is read by its deterministic id (tenant, site, anchor, locale), so a
    /// second boot finds what the first wrote. No provider has a unique index to fall back on.
    /// </para>
    /// <para>
    /// A site can be excluded with <c>module.contextualhelp.enabled = false</c> (the per-module switch); the feature as a whole is
    /// <c>Help:Enabled</c>, and while that is off this type is not even registered.
    /// </para>
    /// </remarks>
    public sealed class HelpTopicReconciler : ISiteContentReconciler
    {
        /// <summary>The module name the per-module switch is keyed on: <c>module.contextualhelp.enabled</c>.</summary>
        public const string ModuleName = "ContextualHelp";

        private readonly HelpTopicContributorRegistry _registry;
        private readonly IHelpTopicRepository _topics;
        private readonly IFeatureConfigurationSource _featureConfig;
        private readonly ILogger<HelpTopicReconciler> _logger;

        public HelpTopicReconciler(
            HelpTopicContributorRegistry registry,
            IHelpTopicRepository topics,
            IFeatureConfigurationSource featureConfig,
            ILogger<HelpTopicReconciler> logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _topics = topics ?? throw new ArgumentNullException(nameof(topics));
            _featureConfig = featureConfig ?? throw new ArgumentNullException(nameof(featureConfig));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => "Help topics";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;

            if (ModuleFeatureFlags.IsDisabledByConfig(_featureConfig, ModuleName))
            {
                _logger.LogInformation("Help topics are not reconciled on site {SiteId}: {Key} is false.", siteId, ModuleFeatureFlags.FeatureKey(ModuleName));
                return;
            }

            var created = new List<string>();
            var updated = new List<string>();
            var foreign = new List<string>();

            foreach (var topic in _registry.Topics)
            {
                var contribution = topic.Contribution;
                var existing = await _topics.GetAsync(contribution.Anchor, contribution.Locale, ct);

                if (existing is null)
                {
                    await _topics.AddAsync(HelpTopicAggregate.Seed(context.TenantId, siteId, contribution, topic.ContributorId, DateTime.UtcNow), ct);
                    created.Add(Describe(contribution.Anchor, contribution.Locale));
                    continue;
                }

                if (existing.TryApplySeedUpdate(contribution, topic.ContributorId, DateTime.UtcNow))
                {
                    await _topics.UpdateAsync(existing, ct);
                    updated.Add(Describe(contribution.Anchor, contribution.Locale));
                    continue;
                }

                if (existing.IsSeeded && !string.Equals(existing.SeedContributorId, topic.ContributorId, StringComparison.Ordinal))
                {
                    foreign.Add($"{Describe(contribution.Anchor, contribution.Locale)} (seeded by '{existing.SeedContributorId}', now shipped by '{topic.ContributorId}')");
                }
            }

            if (created.Count > 0)
            {
                _logger.LogInformation("Created {Count} help topic(s) on site {SiteId}: {Topics}", created.Count, siteId, string.Join(", ", created));
            }

            if (updated.Count > 0)
            {
                _logger.LogInformation("Updated {Count} seeded help topic(s) on site {SiteId}: {Topics}", updated.Count, siteId, string.Join(", ", updated));
            }

            if (foreign.Count > 0)
            {
                // Not an error: it is the refusal working. An operator hands a row over with "reset to seed".
                _logger.LogWarning("Left {Count} help topic(s) on site {SiteId} untouched because another contributor seeded them: {Topics}",
                    foreign.Count, siteId, string.Join(", ", foreign));
            }
        }

        private static string Describe(string anchor, string locale) => anchor + " [" + locale + "]";
    }
}
