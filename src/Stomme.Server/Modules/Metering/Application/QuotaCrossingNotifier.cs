#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models.Notifications;
using Stomme.Modules.Metering.Domain;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Tells the site's operators - its administrators and the Host Users - when a quota crosses its warning threshold or its
    /// limit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Once per crossing, and the aggregate is what guarantees it.</b> <c>QuotaAggregate.Apply</c> returns a
    /// crossing only the FIRST time a fold passes the threshold in a window, and it records that in
    /// <c>WarningRaised</c>/<c>ExceededRaised</c>, which the window roll and a granted override reset. So "once"
    /// is a property of the persisted counter, not of this notifier's memory - a second host, or a restart, cannot
    /// re-raise it.
    /// </para>
    /// <para>
    /// <b>Why this is NOT a domain-event handler, which is what the design first specified.</b> Measured while
    /// building: <c>NotificationDispatcher.DispatchAsync</c> persists the request AND calls
    /// <c>IUnitOfWork.SaveChangesAsync</c> itself. A domain-event handler is drained BY <c>SaveChangesAsync</c>
    /// (D40), and the handler contract is that a handler stages and never commits - so dispatching from one would
    /// be a re-entrant save inside the drain of the save that triggered it. The crossing is therefore dispatched by
    /// the recorder AFTER its fold has committed, which is the same instant a handler would have seen, without the
    /// re-entrancy. The contract gate keeps its meaning instead of gaining an exemption.
    /// </para>
    /// <para>
    /// <b>Recipients are the site's operators by user id, never an address.</b> The dispatcher rejects an external
    /// address unless the site opts in, and an endpoint recipient outright; a quota warning has no business being
    /// the first thing to test that. The operators are <see cref="SiteOperators"/>'s: the Host Users are among them because a
    /// fresh install's site has no administrator at all, so a warning to administrators alone reached nobody.
    /// </para>
    /// </remarks>
    public sealed class QuotaCrossingNotifier
    {
        private readonly INotificationDispatcher? _dispatcher;
        private readonly IUserRoleRepository _userRoles;
        private readonly ILogger<QuotaCrossingNotifier> _logger;

        /// <remarks>
        /// The dispatcher is OPTIONAL. Metering depends on the producer PORT in <c>Stomme.Shared</c>, never on the
        /// Notifications module, and a host composed without one must still be able to METER - recording is the
        /// module's job and telling someone about it is advisory. Taking it as a required dependency would make a
        /// missing notifications registration break every producer's call to <c>IUsageRecorder</c>.
        /// </remarks>
        public QuotaCrossingNotifier(IUserRoleRepository userRoles, ILogger<QuotaCrossingNotifier> logger, INotificationDispatcher? dispatcher = null)
        {
            _dispatcher = dispatcher;
            _userRoles = userRoles;
            _logger = logger;
        }

        public async Task NotifyAsync(QuotaCrossing crossing, int siteId, string metric, long consumed, long limit, string period, CancellationToken ct = default)
        {
            if (crossing == QuotaCrossing.None)
            {
                return;
            }

            if (_dispatcher is null)
            {
                _logger.LogInformation("The quota '{Metric}' crossed {Crossing} on site {SiteId}; no notification dispatcher is registered, so nobody is told.", metric, crossing, siteId);
                return;
            }

            try
            {
                var operators = await _userRoles.GetSiteOperatorIdsAsync(siteId);

                if (operators.Count == 0)
                {
                    _logger.LogWarning("The quota '{Metric}' crossed {Crossing} on site {SiteId}, but the site has no operator to tell.", metric, crossing, siteId);
                    return;
                }

                var key = crossing == QuotaCrossing.Exceeded
                    ? MeteringTemplateCatalogue.QuotaExceededKey
                    : MeteringTemplateCatalogue.QuotaWarningKey;

                await _dispatcher.DispatchAsync(
                    new NotificationDispatchRequest(
                        key,
                        operators.Select(userId => new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(CultureInfo.InvariantCulture))).ToList(),
                        new[] { NotificationChannelKind.InApp },
                        new[]
                        {
                            new DispatchParameter("metric", metric),
                            new DispatchParameter("period", period),
                            new DispatchParameter("consumed", consumed.ToString(CultureInfo.InvariantCulture)),
                            new DispatchParameter("limit", limit.ToString(CultureInfo.InvariantCulture)),
                        },
                        crossing == QuotaCrossing.Exceeded ? NotificationSeverity.Warning : NotificationSeverity.Information,
                        "metering.quota"),
                    ct);
            }
            catch (Exception ex)
            {
                // A quota crossing is recorded in the counter whether or not anyone could be told. Failing the
                // producer's call because a notification could not be queued would turn an advisory into an outage.
                _logger.LogError(ex, "The quota '{Metric}' crossed {Crossing} on site {SiteId}, but the notification could not be queued.", metric, crossing, siteId);
            }
        }
    }

    /// <summary>The two templates this module contributes to BL-78's per-site template reconciliation.</summary>
    public static class MeteringTemplateCatalogue
    {
        public const string QuotaWarningKey = "metering.quota-warning";

        public const string QuotaExceededKey = "metering.quota-exceeded";

        public static IReadOnlyList<NotificationTemplateAggregate> All(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            var placeholders = new PlaceholderSchema(new[] { "metric", "period", "consumed", "limit" });
            return new[]
            {
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, QuotaWarningKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Quota warning: {{ metric }}",
                            "The {{ period }} quota for {{ metric }} has reached {{ consumed }} of {{ limit }} units."))
                    },
                    placeholders: placeholders),
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, QuotaExceededKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp, ChannelKind.Webhook },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Quota exceeded: {{ metric }}",
                            "The {{ period }} quota for {{ metric }} has been exceeded: {{ consumed }} of {{ limit }} units."))
                    },
                    placeholders: placeholders),
            };
        }
    }

    /// <summary>Contributes <see cref="MeteringTemplateCatalogue"/> to the per-site template reconciliation (BL-78's seam).</summary>
    public sealed class MeteringTemplateSource : INotificationTemplateSource
    {
        public string Name => "Metering quota templates";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            MeteringTemplateCatalogue.All(tenantId, siteId);
    }
}
