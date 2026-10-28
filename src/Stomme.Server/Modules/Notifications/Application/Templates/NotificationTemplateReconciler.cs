#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications.Persistence;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Application.Templates
{
    /// <summary>
    /// A contributor of built-in notification templates. The module ships <see cref="BuiltInNotificationTemplateSource"/>
    /// (<c>generic.message</c>); every later module that notifies brings its own source (D100 point 5).
    /// </summary>
    public interface INotificationTemplateSource
    {
        /// <summary>A human name for log lines.</summary>
        string Name { get; }

        /// <summary>The templates this source declares for a site, each with a fresh id.</summary>
        IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId);
    }

    /// <summary>The module's own catalogue: <see cref="NotificationTemplateCatalogue.All"/>.</summary>
    public sealed class BuiltInNotificationTemplateSource : INotificationTemplateSource
    {
        public string Name => "Notifications built-ins";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            NotificationTemplateCatalogue.All(tenantId, siteId);
    }

    /// <summary>
    /// Creates, per site, every declared template the site does not have yet - keyed by <c>KeyNormalized</c> - and never
    /// touches one that exists, so an operator's edit survives every boot (D100 point 5: additive, create-if-absent).
    /// the predecessor platform's seeder was already create-if-absent but ran once for the default tenant from a hosted service; here it
    /// runs per SITE on the boot/upgrade path through the framework's <see cref="SiteContentReconciliationRunner"/>.
    /// </summary>
    /// <remarks>
    /// Residue (not solved here): the module store has no unique index on (site, key) on any provider, so two hosts
    /// booting the same upgrade at the same instant could each create the same absent template. The boot path holds the
    /// installer's "StartUp" distributed lock, which serialises instances that share a lock manager.
    /// </remarks>
    public sealed class NotificationTemplateReconciler : ISiteContentReconciler
    {
        private readonly IEnumerable<INotificationTemplateSource> _sources;
        private readonly INotificationTemplateRepository _templates;
        private readonly ILogger<NotificationTemplateReconciler> _logger;

        public NotificationTemplateReconciler(
            IEnumerable<INotificationTemplateSource> sources,
            INotificationTemplateRepository templates,
            ILogger<NotificationTemplateReconciler> logger)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
            _templates = templates ?? throw new ArgumentNullException(nameof(templates));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => "Notification templates";

        public async Task ReconcileAsync(SiteContentReconciliationContext context, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var siteId = context.Site.SiteId;
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var created = new List<string>();

            foreach (var source in _sources)
            {
                foreach (var template in source.CreateTemplates(context.TenantId, siteId))
                {
                    if (!declared.Add(template.KeyNormalized))
                    {
                        _logger.LogWarning("Notification template '{Key}' from '{Source}' is declared more than once; the first declaration wins.", template.Key, source.Name);
                        continue;
                    }

                    if (await _templates.GetByKeyAsync(siteId, template.Key, ct) is not null)
                    {
                        continue; // exists - possibly edited by an operator; never re-processed
                    }

                    await _templates.AddAsync(template, ct);
                    created.Add(template.Key);
                }
            }

            if (created.Count > 0)
            {
                _logger.LogInformation("Created {Count} notification template(s) on site {SiteId}: {Keys}", created.Count, siteId, string.Join(", ", created));
            }
        }
    }
}
