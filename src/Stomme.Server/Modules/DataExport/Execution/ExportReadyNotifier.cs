#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;
using Stomme.Modules.DataExport.Domain;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Modules.Notifications.Domain;
using Stomme.Notifications;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>
    /// Tells the requester their export is ready, through the notification producer port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Best-effort, and after the fact.</b> It is called by the processor once the Ready transition has been SAVED,
    /// never from a domain-event handler: the dispatcher persists and commits on its own, and a handler is drained BY
    /// a commit, so dispatching from one would be a save re-entering the save that raised it. A notice that
    /// cannot be queued is logged and swallowed - the export is ready whether or not anyone could be told.
    /// </para>
    /// <para>
    /// <b>The recipient is the requester, by USER ID, and nobody else.</b> Not an address (the dispatcher refuses an
    /// external one unless the site opts in), and not the site's administrators: that an export exists, and of what,
    /// is the requester's business. The notice carries the format, the row count and the MODULE's download route - a
    /// route that authorises the person who follows it - and never the artifact or a framework file URL.
    /// </para>
    /// <para>The dispatcher is optional: with Notifications absent the module still renders exports.</para>
    /// </remarks>
    public sealed class ExportReadyNotifier
    {
        private readonly ILogger<ExportReadyNotifier> _logger;
        private readonly INotificationDispatcher? _dispatcher;

        public ExportReadyNotifier(ILogger<ExportReadyNotifier> logger, INotificationDispatcher? dispatcher = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _dispatcher = dispatcher;
        }

        public async Task NotifyAsync(ExportJobAggregate job, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(job);

            if (_dispatcher is null || job.Status != ExportJobStatus.Ready)
            {
                return;
            }

            try
            {
                await _dispatcher.DispatchAsync(
                    new NotificationDispatchRequest(
                        ExportTemplateCatalogue.ReadyKey,
                        new[] { new DispatchRecipient(NotificationRecipientKind.User, job.RequesterUserId.ToString(CultureInfo.InvariantCulture)) },
                        new[] { NotificationChannelKind.InApp },
                        new[]
                        {
                            new DispatchParameter("source", job.SourceKey),
                            new DispatchParameter("format", job.Format.ToString()),
                            new DispatchParameter("rows", (job.RowCount ?? 0).ToString(CultureInfo.InvariantCulture)),
                            new DispatchParameter("download", "/api/DataExport/" + job.Id.ToString("D") + "/download"),
                        },
                        NotificationSeverity.Information,
                        "dataexport.ready"),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export {JobId} is ready, but its requester could not be notified.", job.Id);
            }
        }

        /// <summary>A schedule a run paused - its owner and the site's operators are told, in app; nothing about the data.</summary>
        public async Task NotifySchedulePausedAsync(ExportScheduleAggregate schedule, System.Collections.Generic.IReadOnlyList<int> recipients, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            if (_dispatcher is null || recipients is null || recipients.Count == 0)
            {
                return;
            }

            try
            {
                await _dispatcher.DispatchAsync(
                    new NotificationDispatchRequest(
                        ExportTemplateCatalogue.SchedulePausedKey,
                        recipients.Select(id => new DispatchRecipient(NotificationRecipientKind.User, id.ToString(CultureInfo.InvariantCulture))).ToArray(),
                        new[] { NotificationChannelKind.InApp },
                        new[]
                        {
                            new DispatchParameter("schedule", schedule.Name),
                            new DispatchParameter("reason", schedule.PauseReason == ExportSchedulePauseReason.SourceUnavailable
                                ? "its source is no longer available"
                                : "its owner may no longer make this export"),
                        },
                        NotificationSeverity.Warning,
                        "dataexport.schedule-paused"),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Export schedule {ScheduleId} was paused, but no one could be notified.", schedule.Id);
            }
        }
    }

    /// <summary>The one template this module contributes to the per-site template reconciliation.</summary>
    public static class ExportTemplateCatalogue
    {
        public const string ReadyKey = "dataexport.ready";

        /// <summary>A run found a schedule's owner no longer allowed, or its source gone, and paused it.</summary>
        public const string SchedulePausedKey = "dataexport.schedule-paused";

        public static IReadOnlyList<NotificationTemplateAggregate> All(TenantId tenantId, int siteId)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            return new[]
            {
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, SchedulePausedKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "An export schedule was paused: {{ schedule }}",
                            "The export schedule \"{{ schedule }}\" was paused because {{ reason }}. Its owner or a manager can resume it on the data export page."))
                    },
                    placeholders: new PlaceholderSchema(new[] { "schedule", "reason" })),
                NotificationTemplateAggregate.Create(
                    Guid.NewGuid(), tenantId, siteId, ReadyKey,
                    supportedChannels: new[] { ChannelKind.Email, ChannelKind.InApp },
                    contents: new[]
                    {
                        new LocalizedContent(new Locale("en"), new Content(
                            "Your export is ready: {{ source }}",
                            "Your {{ format }} export of {{ source }} is ready ({{ rows }} rows). Download it from {{ download }} - you will be asked to sign in, and the link works for you alone."))
                    },
                    placeholders: new PlaceholderSchema(new[] { "source", "format", "rows", "download" })),
            };
        }
    }

    /// <summary>Contributes <see cref="ExportTemplateCatalogue"/> to the per-site template reconciliation.</summary>
    public sealed class ExportTemplateSource : INotificationTemplateSource
    {
        public string Name => "Data export templates";

        public IReadOnlyList<NotificationTemplateAggregate> CreateTemplates(TenantId tenantId, int siteId) =>
            ExportTemplateCatalogue.All(tenantId, siteId);
    }
}
