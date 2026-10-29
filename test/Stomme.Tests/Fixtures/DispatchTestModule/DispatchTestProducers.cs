using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Notifications;

namespace Stomme.Tests.Fixtures.DispatchTestModule
{
    /// <summary>
    /// A STUB DEPENDENT of the Notifications module: it references ONLY the published port in
    /// <c>Stomme.Shared</c> - <see cref="INotificationDispatcher"/> and its DTOs - and never a Notifications-module type.
    /// <c>NotificationDispatcherContractTests</c> asserts that reflectively (member signatures) and syntactically (this
    /// folder's source), then drives it to a fake channel. Its four methods are the four producer SHAPES the survey found
    /// in the predecessor platform's dependents: a HealthCheck alert, a DataExport export-ready note, ControlFlow's runtime-PARSED request
    /// (built from untyped input) and a CloudUsage threshold warning.
    /// </summary>
    public sealed class DispatchTestProducers
    {
        public const string TemplateKey = "generic.message";

        private readonly INotificationDispatcher _dispatcher;

        public DispatchTestProducers(INotificationDispatcher dispatcher) =>
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        /// <summary>HealthCheck: a critical alert to a site user over in-app and webhook.</summary>
        public Task<Guid> HealthCheckAlertAsync(int userId, string checkName, CancellationToken ct = default) =>
            _dispatcher.DispatchAsync(new NotificationDispatchRequest(
                TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(CultureInfo.InvariantCulture))],
                [NotificationChannelKind.InApp, NotificationChannelKind.Webhook],
                [new DispatchParameter("subject", $"Health check '{checkName}' is unhealthy"), new DispatchParameter("body", $"{checkName} failed its probe.")],
                NotificationSeverity.Critical,
                Topic: "health"), ct);

        /// <summary>DataExport: the export is ready - an informational e-mail to the requesting user.</summary>
        public Task<Guid> ExportReadyAsync(int userId, string exportName, CancellationToken ct = default) =>
            _dispatcher.DispatchAsync(new NotificationDispatchRequest(
                TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(CultureInfo.InvariantCulture))],
                [NotificationChannelKind.Email],
                [new DispatchParameter("subject", $"Your export '{exportName}' is ready"), new DispatchParameter("body", "Download it from the exports page.")]), ct);

        /// <summary>
        /// ControlFlow: a "notify" step whose request is PARSED at runtime from an untyped property bag (what a pipeline
        /// definition carries) - recipient kind, channels and parameters are all strings until here.
        /// </summary>
        public Task<Guid> ParsedStepAsync(IReadOnlyDictionary<string, object> step, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(step);
            var recipients = ((IEnumerable<object>)step["recipients"])
                .Select(raw => raw.ToString()!.Split(':', 2))
                .Select(parts => new DispatchRecipient(Enum.Parse<NotificationRecipientKind>(parts[0], ignoreCase: true), parts[1]))
                .ToList();
            var channels = step["channels"].ToString()!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Enum.Parse<NotificationChannelKind>(name, ignoreCase: true))
                .ToList();
            var parameters = ((IDictionary<string, object>)step["parameters"])
                .Select(pair => new DispatchParameter(pair.Key, Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty))
                .ToList();
            var severity = step.TryGetValue("severity", out var rawSeverity)
                ? Enum.Parse<NotificationSeverity>(rawSeverity.ToString()!, ignoreCase: true)
                : NotificationSeverity.Information;

            return _dispatcher.DispatchAsync(new NotificationDispatchRequest(step["template"].ToString()!, recipients, channels, parameters, severity), ct);
        }

        /// <summary>CloudUsage: a spend threshold warning to a site user in-app.</summary>
        public Task<Guid> CloudUsageThresholdAsync(int userId, decimal spent, decimal budget, CancellationToken ct = default) =>
            _dispatcher.DispatchAsync(new NotificationDispatchRequest(
                TemplateKey,
                [new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(CultureInfo.InvariantCulture))],
                [NotificationChannelKind.InApp],
                [
                    new DispatchParameter("subject", "Cloud spend warning"),
                    new DispatchParameter("body", string.Create(CultureInfo.InvariantCulture, $"Spent {spent:0.00} of {budget:0.00}.")),
                ],
                NotificationSeverity.Warning,
                Topic: "cloud-usage"), ct);
    }
}
