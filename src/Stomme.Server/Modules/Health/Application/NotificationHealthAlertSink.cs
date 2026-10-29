#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Infrastructure.Security;
using Stomme.Models.Notifications;
using Stomme.Modules.Health.Domain;
using Stomme.Notifications;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Health.Application
{
    /// <summary>
    /// Sends a raised or cleared alert to the people who can act on it, through the notification producer port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The exception MESSAGE never travels.</b> A store failure's description carries host, port, database and
    /// user; putting it in a notification mails that to every recipient. The dispatch carries the exception TYPE,
    /// the latency and how long the subject has been bad — enough to know what happened and how urgent it is —
    /// and the message stays on the row, behind <c>health:view</c>.
    /// </para>
    /// <para>
    /// <b>Who is told depends on the SCOPE, which is a refinement.</b> The design said Host users
    /// for everything. A Site-scope subject is that site's own business — its configured mail server — and its
    /// operators are the people who can fix it, so they are told: its administrators and the Host Users
    /// (<see cref="SiteOperators"/>; a fresh install's site has no administrator at all). A Tenant- or Installation-scope subject
    /// describes a dependency SHARED across sites, which is the operator's business and nobody else's, so those
    /// go to Host users only. Telling every site's administrators that the shared database is down would be one
    /// alert per site for one fault, which is the mail-bomb this row exists to avoid.
    /// </para>
    /// <para>
    /// <b>Recipients are de-duplicated and addressed by USER ID, never by address.</b> One person holding two
    /// roles is one recipient.
    /// </para>
    /// <para>
    /// <b>Zero recipients is LOGGED, not silently returned.</b> Returning quietly would mean an installation with
    /// no Host user alerted nobody about anything and looked healthy while doing it.
    /// </para>
    /// </remarks>
    public sealed class NotificationHealthAlertSink : IHealthAlertSink
    {
        private readonly INotificationDispatcher? _dispatcher;
        private readonly IUserRoleRepository _userRoles;
        private readonly ILogger<NotificationHealthAlertSink> _logger;

        /// <remarks>
        /// The dispatcher is OPTIONAL, for the reason metering's notifier gives: health depends on the producer
        /// PORT, never on the Notifications module, and a host composed without one must still MONITOR. Recording
        /// is the job; telling someone is advisory.
        /// </remarks>
        public NotificationHealthAlertSink(
            IUserRoleRepository userRoles,
            ILogger<NotificationHealthAlertSink> logger,
            INotificationDispatcher? dispatcher = null)
        {
            _userRoles = userRoles;
            _logger = logger;
            _dispatcher = dispatcher;
        }

        public Task RaisedAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default) =>
            SendAsync(notice, raised: true, cancellationToken);

        public Task RecoveredAsync(HealthAlertNotice notice, CancellationToken cancellationToken = default) =>
            SendAsync(notice, raised: false, cancellationToken);

        private async Task SendAsync(HealthAlertNotice notice, bool raised, CancellationToken cancellationToken)
        {
            if (_dispatcher is null)
            {
                _logger.LogInformation(
                    "Health subject {Subject} {What} on site {SiteId}; no notification dispatcher is registered, so nobody is told.",
                    notice.Subject.Value, raised ? "raised an alert" : "recovered", notice.SiteId);
                return;
            }

            try
            {
                var recipients = await RecipientsAsync(notice).ConfigureAwait(false);
                if (recipients.Count == 0)
                {
                    _logger.LogWarning(
                        "Health subject {Subject} {What} ({Scope} scope, site {SiteId}), but there is nobody to tell: no {Role} user exists.",
                        notice.Subject.Value, raised ? "raised an alert" : "recovered", notice.Scope, notice.SiteId,
                        notice.Scope == HealthProbeScope.Site ? $"{RoleNames.Admin} or {RoleNames.Host}" : RoleNames.Host);
                    return;
                }

                await _dispatcher.DispatchAsync(
                    new NotificationDispatchRequest(
                        HealthTemplateCatalogue.AlertKey,
                        recipients.Select(userId => new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(CultureInfo.InvariantCulture))).ToList(),
                        new[] { NotificationChannelKind.InApp },
                        Parameters(notice, raised),
                        raised && notice.State == HealthState.Unhealthy ? NotificationSeverity.Warning : NotificationSeverity.Information,
                        "health.alert"),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The transition is recorded on the row whether or not anyone could be told. Throwing here would
                // turn "the mail queue is busy" into "health monitoring stopped".
                _logger.LogError(ex,
                    "Health subject {Subject} changed on site {SiteId}, but the notification could not be queued.",
                    notice.Subject.Value, notice.SiteId);
            }
        }

        private async Task<IReadOnlyList<int>> RecipientsAsync(HealthAlertNotice notice)
        {
            // A Site-scope fault is the site's operators'; anything wider is the Host Users' alone. See the remarks.
            if (notice.Scope == HealthProbeScope.Site)
            {
                return await _userRoles.GetSiteOperatorIdsAsync(notice.SiteId);
            }

            var roles = await _userRoles.GetUserRolesAsync(RoleNames.Host, -1);
            return roles.Select(item => item.UserId).Distinct().ToList();
        }

        private static IReadOnlyList<DispatchParameter> Parameters(HealthAlertNotice notice, bool raised)
        {
            var since = notice.Row.BadSinceUtc is { } bad
                ? ((long)(DateTime.UtcNow - HealthTime.AsUtc(bad)).TotalMinutes).ToString(CultureInfo.InvariantCulture)
                : "0";

            return new[]
            {
                new DispatchParameter("subject", notice.Subject.Value),
                new DispatchParameter("dependency", notice.Result.Dependency),
                new DispatchParameter("state", raised ? notice.State.ToString() : nameof(HealthState.Healthy)),
                new DispatchParameter("previousState", notice.PreviousState.ToString()),
                new DispatchParameter("scope", notice.Scope.ToString()),
                // The TYPE, never the message: the message names the host, the database and the user.
                new DispatchParameter("exceptionType", Value(notice, "exceptionType")),
                new DispatchParameter("latencyMs", Value(notice, "latencyMs")),
                new DispatchParameter("sinceMinutes", since)
            };

            static string Value(HealthAlertNotice notice, string key) =>
                notice.Result.Data is not null && notice.Result.Data.TryGetValue(key, out var value) ? value : string.Empty;
        }
    }
}
